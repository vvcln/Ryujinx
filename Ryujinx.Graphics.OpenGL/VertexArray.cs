using OpenTK.Graphics.OpenGL;
using Ryujinx.Graphics.GAL;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.OpenGL
{
    class VertexArray : IDisposable
    {
        public int Handle { get; private set; }

        private bool _needsAttribsUpdate;

        private readonly VertexAttribDescriptor[] _vertexAttribs;
        private readonly VertexBufferDescriptor[] _vertexBuffers;

        private int _vertexAttribsCount;
        private int _vertexBuffersCount;
        private int _minVertexCount;

        private uint _vertexAttribsInUse;
        private uint _vertexBuffersInUse;
        private uint _vertexBuffersLimited;

        private BufferRange _indexBuffer;
        private BufferHandle _tempIndexBuffer;
        private BufferHandle _tempVertexBuffer;
        private int _tempVertexBufferSize;

        public VertexArray()
        {
            Handle = GL.GenVertexArray();

            _vertexAttribs = new VertexAttribDescriptor[Constants.MaxVertexAttribs];
            _vertexBuffers = new VertexBufferDescriptor[Constants.MaxVertexBuffers];

            _tempIndexBuffer = Buffer.Create();
        }

        public void Bind()
        {
            GL.BindVertexArray(Handle);
        }

        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            int minVertexCount = int.MaxValue;

            int bindingIndex;
            for (bindingIndex = 0; bindingIndex < vertexBuffers.Length; bindingIndex++)
            {
                VertexBufferDescriptor vb = vertexBuffers[bindingIndex];

                if (vb.Buffer.Handle != BufferHandle.Null)
                {
                    int vertexCount = vb.Stride <= 0 ? 0 : vb.Buffer.Size / vb.Stride;
                    if (minVertexCount > vertexCount)
                    {
                        minVertexCount = vertexCount;
                    }

                    GL.BindVertexBuffer(bindingIndex, vb.Buffer.Handle.ToInt32(), (IntPtr)vb.Buffer.Offset, vb.Stride);
                    GL.VertexBindingDivisor(bindingIndex, vb.Divisor);
                    _vertexBuffersInUse |= 1u << bindingIndex;
                }
                else
                {
                    if ((_vertexBuffersInUse & (1u << bindingIndex)) != 0)
                    {
                        GL.BindVertexBuffer(bindingIndex, 0, IntPtr.Zero, 0);
                        _vertexBuffersInUse &= ~(1u << bindingIndex);
                    }
                }

                _vertexBuffers[bindingIndex] = vb;
            }

            _vertexBuffersCount = bindingIndex;
            _minVertexCount = minVertexCount;
            _needsAttribsUpdate = true;
        }

        public void SetVertexAttributes(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            int index = 0;

            for (; index < vertexAttribs.Length; index++)
            {
                VertexAttribDescriptor attrib = vertexAttribs[index];

                if (attrib.Equals(_vertexAttribs[index]))
                {
                    continue;
                }

                FormatInfo fmtInfo = FormatTable.GetFormatInfo(attrib.Format);

                if (attrib.IsZero)
                {
                    // Disabling the attribute causes the shader to read a constant value.
                    // We currently set the constant to (0, 0, 0, 0).
                    DisableVertexAttrib(index);
                }
                else
                {
                    EnableVertexAttrib(index);
                }

                int offset = attrib.Offset;
                int size   = fmtInfo.Components;

                bool isFloat = fmtInfo.PixelType == PixelType.Float ||
                               fmtInfo.PixelType == PixelType.HalfFloat;

                if (isFloat || fmtInfo.Normalized || fmtInfo.Scaled)
                {
                    VertexAttribType type = (VertexAttribType)fmtInfo.PixelType;

                    GL.VertexAttribFormat(index, size, type, fmtInfo.Normalized, offset);
                }
                else
                {
                    VertexAttribIntegerType type = (VertexAttribIntegerType)fmtInfo.PixelType;

                    GL.VertexAttribIFormat(index, size, type, offset);
                }

                GL.VertexAttribBinding(index, attrib.BufferIndex);

                _vertexAttribs[index] = attrib;
            }

            _vertexAttribsCount = index;

            for (; index < Constants.MaxVertexAttribs; index++)
            {
                DisableVertexAttrib(index);
            }
        }

        public void SetIndexBuffer(BufferRange range)
        {
            _indexBuffer = range;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, range.Handle.ToInt32());
        }

        public void SetRangeOfIndexBuffer()
        {
            Buffer.Resize(_tempIndexBuffer, _indexBuffer.Size);
            Buffer.Copy(_indexBuffer.Handle, _tempIndexBuffer, _indexBuffer.Offset, 0, _indexBuffer.Size);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _tempIndexBuffer.ToInt32());
        }

        public void RestoreIndexBuffer()
        {
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer.Handle.ToInt32());
        }

        public void PreDraw(int vertexCount)
        {
            LimitVertexBuffers(vertexCount);
            Validate();
        }

        public void PreDrawVbUnbounded()
        {
            UnlimitVertexBuffers();
            Validate();
        }

        public void LimitVertexBuffers(int vertexCount)
        {
            // Is it possible for the draw to fetch outside the bounds of any vertex buffer currently bound?

            if (vertexCount <= _minVertexCount)
            {
                return;
            }

            // If the draw can fetch out of bounds, let's ensure that it will only fetch zeros rather than memory garbage.

            int currentTempVbOffset = 0;
            uint buffersInUse = _vertexBuffersInUse;

            while (buffersInUse != 0)
            {
                int vbIndex = BitOperations.TrailingZeroCount(buffersInUse);

                ref var vb = ref _vertexBuffers[vbIndex];

                int requiredSize = vertexCount * vb.Stride;

                if (vb.Buffer.Size < requiredSize)
                {
                    BufferHandle tempVertexBuffer = EnsureTempVertexBufferSize(currentTempVbOffset + requiredSize);

                    Buffer.Copy(vb.Buffer.Handle, tempVertexBuffer, vb.Buffer.Offset, currentTempVbOffset, vb.Buffer.Size);
                    Buffer.Clear(tempVertexBuffer, currentTempVbOffset + vb.Buffer.Size, requiredSize - vb.Buffer.Size, 0);

                    GL.BindVertexBuffer(vbIndex, tempVertexBuffer.ToInt32(), (IntPtr)currentTempVbOffset, vb.Stride);

                    currentTempVbOffset += requiredSize;
                    _vertexBuffersLimited |= 1u << vbIndex;
                }

                buffersInUse &= ~(1u << vbIndex);
            }
        }

        private BufferHandle EnsureTempVertexBufferSize(int size)
        {
            BufferHandle tempVertexBuffer = _tempVertexBuffer;

            if (_tempVertexBufferSize < size)
            {
                _tempVertexBufferSize = size;

                if (tempVertexBuffer == BufferHandle.Null)
                {
                    tempVertexBuffer = Buffer.Create(size);
                    _tempVertexBuffer = tempVertexBuffer;
                    return tempVertexBuffer;
                }

                Buffer.Resize(_tempVertexBuffer, size);
            }

            return tempVertexBuffer;
        }

        public void UnlimitVertexBuffers()
        {
            uint buffersLimited = _vertexBuffersLimited;

            if (buffersLimited == 0)
            {
                return;
            }

            while (buffersLimited != 0)
            {
                int vbIndex = BitOperations.TrailingZeroCount(buffersLimited);

                ref var vb = ref _vertexBuffers[vbIndex];

                GL.BindVertexBuffer(vbIndex, vb.Buffer.Handle.ToInt32(), (IntPtr)vb.Buffer.Offset, vb.Stride);

                buffersLimited &= ~(1u << vbIndex);
            }

            _vertexBuffersLimited = 0;
        }

        public void Validate()
        {
            for (int attribIndex = 0; attribIndex < _vertexAttribsCount; attribIndex++)
            {
                VertexAttribDescriptor attrib = _vertexAttribs[attribIndex];

                if (!attrib.IsZero)
                {
                    if ((uint)attrib.BufferIndex >= _vertexBuffersCount)
                    {
                        DisableVertexAttrib(attribIndex);
                        continue;
                    }

                    if (_vertexBuffers[attrib.BufferIndex].Buffer.Handle == BufferHandle.Null)
                    {
                        DisableVertexAttrib(attribIndex);
                        continue;
                    }

                    if (_needsAttribsUpdate)
                    {
                        EnableVertexAttrib(attribIndex);
                    }
                }
            }

            _needsAttribsUpdate = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnableVertexAttrib(int index)
        {
            uint mask = 1u << index;

            if ((_vertexAttribsInUse & mask) == 0)
            {
                _vertexAttribsInUse |= mask;
                GL.EnableVertexAttribArray(index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DisableVertexAttrib(int index)
        {
            uint mask = 1u << index;

            if ((_vertexAttribsInUse & mask) != 0)
            {
                _vertexAttribsInUse &= ~mask;
                GL.DisableVertexAttribArray(index);
                GL.VertexAttrib4(index, 0f, 0f, 0f, 1f);
            }
        }

        public void Dispose()
        {
            if (Handle != 0)
            {
                GL.DeleteVertexArray(Handle);

                Handle = 0;
            }
        }
    }
}
