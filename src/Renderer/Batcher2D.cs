﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using ClassicUO.IO;
using ClassicUO.IO.Resources;

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using SDL2;

namespace ClassicUO.Renderer
{
    internal class Batcher2D
    {
        private const int MAX_SPRITES = 0x800;
        private const int MAX_VERTICES = MAX_SPRITES * 4;
        private const int MAX_INDICES = MAX_SPRITES * 6;

        private Matrix _projectionMatrix;
        private Matrix _matrixTransformMatrix;
        private Matrix _transformMatrix = Matrix.Identity;
        private BoundingBox _drawingArea;
        private readonly EffectParameter _viewportEffect;
        private readonly EffectParameter _worldMatrixEffect;
        private readonly EffectParameter _drawLightingEffect;
        private readonly EffectParameter _projectionMatrixEffect;
        private readonly EffectTechnique _huesTechnique, _shadowTechnique, _landTechnique;
        private readonly Effect _effect;

        //private readonly DepthStencilState _dss = new DepthStencilState
        //{
        //    DepthBufferEnable = true,
        //    DepthBufferWriteEnable = true
        //};
        //private readonly DepthStencilState _dssStencil = new DepthStencilState
        //{
        //    StencilEnable = true,
        //    StencilFunction = CompareFunction.Always,
        //    StencilPass = StencilOperation.Replace,
        //    DepthBufferEnable = false,
        //};
        private readonly VertexBuffer _vertexBuffer;
        private readonly IndexBuffer _indexBuffer;
        private readonly Texture2D[] _textureInfo;
        private readonly SpriteVertex[] _vertexInfo;
        private bool _started;
        private readonly Vector3 _minVector3 = new Vector3(0, 0, int.MinValue);
        private readonly RasterizerState _rasterizerState;
        private BlendState _blendState;
        private DepthStencilState _stencil;


        private int _numSprites;
        private readonly SpriteVertex[] _vertexBufferUI = new SpriteVertex[4];


        public Batcher2D(GraphicsDevice device)
        {
            GraphicsDevice = device;
            _effect = new Effect(GraphicsDevice, File.ReadAllBytes(Path.Combine(Engine.ExePath, "shaders/IsometricWorld.fxc")));
            float f = (float) FileManager.Hues.HuesCount;
            _effect.Parameters["HuesPerTexture"].SetValue(f);
            _drawLightingEffect = _effect.Parameters["DrawLighting"];
            _projectionMatrixEffect = _effect.Parameters["ProjectionMatrix"];
            _worldMatrixEffect = _effect.Parameters["WorldMatrix"];
            _viewportEffect = _effect.Parameters["Viewport"];
            _huesTechnique = _effect.Techniques["HueTechnique"];
            _shadowTechnique = _effect.Techniques["ShadowSetTechnique"];
            _landTechnique = _effect.Techniques["LandTechnique"];
            _textureInfo = new Texture2D[MAX_SPRITES];
            _vertexInfo = new SpriteVertex[MAX_VERTICES];
            _vertexBuffer = new DynamicVertexBuffer(GraphicsDevice, SpriteVertex.VertexDeclaration, MAX_VERTICES, BufferUsage.WriteOnly);
            _indexBuffer = new IndexBuffer(GraphicsDevice, IndexElementSize.SixteenBits, MAX_INDICES, BufferUsage.WriteOnly);
            _indexBuffer.SetData(GenerateIndexArray());

            _projectionMatrix = new Matrix(0f, //(float)( 2.0 / (double)viewport.Width ) is the actual value we will use
                                           0.0f, 0.0f, 0.0f, 0.0f, 0f, //(float)( -2.0 / (double)viewport.Height ) is the actual value we will use
                                           0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, -1.0f, 1.0f, 0.0f, 1.0f);
            _effect.CurrentTechnique = _huesTechnique;
            _blendState = BlendState.AlphaBlend;

            _rasterizerState = RasterizerState.CullNone;
            _rasterizerState = new RasterizerState()
            {
                CullMode = _rasterizerState.CullMode,
                DepthBias = _rasterizerState.DepthBias,
                FillMode = _rasterizerState.FillMode,
                MultiSampleAntiAlias = _rasterizerState.MultiSampleAntiAlias,
                SlopeScaleDepthBias = _rasterizerState.SlopeScaleDepthBias,
                ScissorTestEnable = true
            };

            _stencil =  DepthStencilState.None;
        }

        public Matrix TransformMatrix => _transformMatrix;

        public GraphicsDevice GraphicsDevice { get; }

        public void SetLightDirection(Vector3 dir)
        {
            _effect.Parameters["lightDirection"].SetValue(dir);
        }

        public void SetLightIntensity(float inte)
        {
            _effect.Parameters["lightIntensity"].SetValue(inte);
        }

        public void EnableLight(bool value)
        {
            _drawLightingEffect.SetValue(value);
        }

        public void Begin()
        {
            EnsureNotStarted();
            _started = true;

            _drawingArea.Min = _minVector3;
            _drawingArea.Max = new Vector3(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height, int.MaxValue);
        }

        public void End()
        {
            EnsureStarted();
            Flush();
            _started = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool DrawSprite(Texture2D texture, SpriteVertex[] vertices, Techniques technique = Techniques.Default)
        {
            if (texture == null || texture.IsDisposed)
                return false;
            bool draw = false;

            for (byte i = 0; i < 4; i++)
            {
                if (_drawingArea.Contains(vertices[i].Position) == ContainmentType.Contains)
                {
                    draw = true;

                    break;
                }
            }

            if (!draw)
                return false;

            if (_numSprites >= MAX_SPRITES)
                Flush();

            _textureInfo[_numSprites] = texture;

            fixed (SpriteVertex* p = &_vertexInfo[_numSprites * 4])
            {
                fixed (SpriteVertex* t = &vertices[0])
                {
                    SpriteVertex* ptr0 = p;
                    ptr0[0] = t[0];
                    ptr0[1] = t[1];
                    ptr0[2] = t[2];
                    ptr0[3] = t[3];
                }
            }

            _numSprites++;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DrawShadow(Texture2D texture, SpriteVertex[] vertices, Vector2 position, bool flip, float z)
        {
            if (texture == null || texture.IsDisposed)
                return;

            if (_numSprites >= MAX_SPRITES)
                Flush();

            float skewHorizTop = (vertices[0].Position.Y - position.Y) * .5f;
            float skewHorizBottom = (vertices[3].Position.Y - position.Y) * .5f;
            vertices[0].Position.X -= skewHorizTop;
            vertices[0].Position.Y -= skewHorizTop;
            vertices[flip ? 2 : 1].Position.X -= skewHorizTop;
            vertices[flip ? 2 : 1].Position.Y -= skewHorizTop;
            vertices[flip ? 1 : 2].Position.X -= skewHorizBottom;
            vertices[flip ? 1 : 2].Position.Y -= skewHorizBottom;
            vertices[3].Position.X -= skewHorizBottom;
            vertices[3].Position.Y -= skewHorizBottom;
            _textureInfo[_numSprites] = texture;

            fixed (SpriteVertex* p = &_vertexInfo[_numSprites * 4])
            {
                fixed (SpriteVertex* t = &vertices[0])
                {
                    SpriteVertex* ptr0 = p;
                    ptr0[0] = t[0];
                    ptr0[1] = t[1];
                    ptr0[2] = t[2];
                    ptr0[3] = t[3];
                }
            }

            _numSprites++;
        }

        public unsafe bool Draw2D(Texture2D texture, Point position, Vector3 hue)
        {
            // TODO: CHECK if this is really needed, since some client datafiles could have  
            // some missing sprites in files...it won't harm client functionality, and will  
            // avoid client crashes in case some sprites are missing from files.
            if (texture == null)
                return false;
            fixed (SpriteVertex* ptr = _vertexBufferUI)
            {
                ptr[0].Position.X = position.X;
                ptr[0].Position.Y = position.Y;
                ptr[0].Position.Z = 0;
                ptr[0].Normal.X = 0;
                ptr[0].Normal.Y = 0;
                ptr[0].Normal.Z = 1;
                ptr[0].TextureCoordinate = Vector3.Zero;
                ptr[1].Position.X = position.X + texture.Width;
                ptr[1].Position.Y = position.Y;
                ptr[1].Position.Z = 0;
                ptr[1].Normal.X = 0;
                ptr[1].Normal.Y = 0;
                ptr[1].Normal.Z = 1;
                ptr[1].TextureCoordinate.X = 1;
                ptr[1].TextureCoordinate.Y = 0;
                ptr[1].TextureCoordinate.Z = 0;
                ptr[2].Position.X = position.X;
                ptr[2].Position.Y = position.Y + texture.Height;
                ptr[2].Position.Z = 0;
                ptr[2].Normal.X = 0;
                ptr[2].Normal.Y = 0;
                ptr[2].Normal.Z = 1;
                ptr[2].TextureCoordinate.X = 0;
                ptr[2].TextureCoordinate.Y = 1;
                ptr[2].TextureCoordinate.Z = 0;
                ptr[3].Position.X = position.X + texture.Width;
                ptr[3].Position.Y = position.Y + texture.Height;
                ptr[3].Position.Z = 0;
                ptr[3].Normal.X = 0;
                ptr[3].Normal.Y = 0;
                ptr[3].Normal.Z = 1;
                ptr[3].TextureCoordinate.X = 1;
                ptr[3].TextureCoordinate.Y = 1;
                ptr[3].TextureCoordinate.Z = 0;
                ptr[0].Hue = ptr[1].Hue = ptr[2].Hue = ptr[3].Hue = hue;
            }

            return DrawSprite(texture, _vertexBufferUI, Techniques.Hued);
        }

        public unsafe bool Draw2D(Texture2D texture, Point position, Rectangle sourceRect, Vector3 hue)
        {
            float minX = sourceRect.X / (float)texture.Width;
            float maxX = (sourceRect.X + sourceRect.Width) / (float)texture.Width;
            float minY = sourceRect.Y / (float)texture.Height;
            float maxY = (sourceRect.Y + sourceRect.Height) / (float)texture.Height;

            fixed (SpriteVertex* ptr = _vertexBufferUI)
            {
                ptr[0].Position.X = position.X;
                ptr[0].Position.Y = position.Y;
                ptr[0].Position.Z = 0;
                ptr[0].Normal.X = 0;
                ptr[0].Normal.Y = 0;
                ptr[0].Normal.Z = 1;
                ptr[0].TextureCoordinate.X = minX;
                ptr[0].TextureCoordinate.Y = minY;
                ptr[0].TextureCoordinate.Z = 0;
                ptr[1].Position.X = position.X + sourceRect.Width;
                ptr[1].Position.Y = position.Y;
                ptr[1].Position.Z = 0;
                ptr[1].Normal.X = 0;
                ptr[1].Normal.Y = 0;
                ptr[1].Normal.Z = 1;
                ptr[1].TextureCoordinate.X = maxX;
                ptr[1].TextureCoordinate.Y = minY;
                ptr[1].TextureCoordinate.Z = 0;
                ptr[2].Position.X = position.X;
                ptr[2].Position.Y = position.Y + sourceRect.Height;
                ptr[2].Position.Z = 0;
                ptr[2].Normal.X = 0;
                ptr[2].Normal.Y = 0;
                ptr[2].Normal.Z = 1;
                ptr[2].TextureCoordinate.X = minX;
                ptr[2].TextureCoordinate.Y = maxY;
                ptr[2].TextureCoordinate.Z = 0;
                ptr[3].Position.X = position.X + sourceRect.Width;
                ptr[3].Position.Y = position.Y + sourceRect.Height;
                ptr[3].Position.Z = 0;
                ptr[3].Normal.X = 0;
                ptr[3].Normal.Y = 0;
                ptr[3].Normal.Z = 1;
                ptr[3].TextureCoordinate.X = maxX;
                ptr[3].TextureCoordinate.Y = maxY;
                ptr[3].TextureCoordinate.Z = 0;
                ptr[0].Hue = ptr[1].Hue = ptr[2].Hue = ptr[3].Hue = hue;
            }

            return DrawSprite(texture, _vertexBufferUI, Techniques.Hued);
        }

        public unsafe bool Draw2D(Texture2D texture, Rectangle destRect, Rectangle sourceRect, Vector3 hue)
        {
            float minX = sourceRect.X / (float)texture.Width, maxX = (sourceRect.X + sourceRect.Width) / (float)texture.Width;
            float minY = sourceRect.Y / (float)texture.Height, maxY = (sourceRect.Y + sourceRect.Height) / (float)texture.Height;

            fixed (SpriteVertex* ptr = _vertexBufferUI)
            {
                ptr[0].Position.X = destRect.X;
                ptr[0].Position.Y = destRect.Y;
                ptr[0].Position.Z = 0;
                ptr[0].Normal.X = 0;
                ptr[0].Normal.Y = 0;
                ptr[0].Normal.Z = 1;
                ptr[0].TextureCoordinate.X = minX;
                ptr[0].TextureCoordinate.Y = minY;
                ptr[0].TextureCoordinate.Z = 0;
                ptr[1].Position.X = destRect.X + destRect.Width;
                ptr[1].Position.Y = destRect.Y;
                ptr[1].Position.Z = 0;
                ptr[1].Normal.X = 0;
                ptr[1].Normal.Y = 0;
                ptr[1].Normal.Z = 1;
                ptr[1].TextureCoordinate.X = maxX;
                ptr[1].TextureCoordinate.Y = minY;
                ptr[1].TextureCoordinate.Z = 0;
                ptr[2].Position.X = destRect.X;
                ptr[2].Position.Y = destRect.Y + destRect.Height;
                ptr[2].Position.Z = 0;
                ptr[2].Normal.X = 0;
                ptr[2].Normal.Y = 0;
                ptr[2].Normal.Z = 1;
                ptr[2].TextureCoordinate.X = minX;
                ptr[2].TextureCoordinate.Y = maxY;
                ptr[2].TextureCoordinate.Z = 0;
                ptr[3].Position.X = destRect.X + destRect.Width;
                ptr[3].Position.Y = destRect.Y + destRect.Height;
                ptr[3].Position.Z = 0;
                ptr[3].Normal.X = 0;
                ptr[3].Normal.Y = 0;
                ptr[3].Normal.Z = 1;
                ptr[3].TextureCoordinate.X = maxX;
                ptr[3].TextureCoordinate.Y = maxY;
                ptr[3].TextureCoordinate.Z = 0;
                ptr[0].Hue = ptr[1].Hue = ptr[2].Hue = ptr[3].Hue = hue;
            }

            return DrawSprite(texture, _vertexBufferUI, Techniques.Hued);
        }

        public unsafe bool Draw2D(Texture2D texture, Rectangle destRect, Vector3 hue)
        {
            fixed (SpriteVertex* ptr = _vertexBufferUI)
            {
                ptr[0].Position.X = destRect.X;
                ptr[0].Position.Y = destRect.Y;
                ptr[0].Position.Z = 0;
                ptr[0].Normal.X = 0;
                ptr[0].Normal.Y = 0;
                ptr[0].Normal.Z = 1;
                ptr[0].TextureCoordinate = Vector3.Zero;
                ptr[1].Position.X = destRect.X + destRect.Width;
                ptr[1].Position.Y = destRect.Y;
                ptr[1].Position.Z = 0;
                ptr[1].Normal.X = 0;
                ptr[1].Normal.Y = 0;
                ptr[1].Normal.Z = 1;
                ptr[1].TextureCoordinate.X = 1;
                ptr[1].TextureCoordinate.Y = 0;
                ptr[1].TextureCoordinate.Z = 0;
                ptr[2].Position.X = destRect.X;
                ptr[2].Position.Y = destRect.Y + destRect.Height;
                ptr[2].Position.Z = 0;
                ptr[2].Normal.X = 0;
                ptr[2].Normal.Y = 0;
                ptr[2].Normal.Z = 1;
                ptr[2].TextureCoordinate.X = 0;
                ptr[2].TextureCoordinate.Y = 1;
                ptr[2].TextureCoordinate.Z = 0;
                ptr[3].Position.X = destRect.X + destRect.Width;
                ptr[3].Position.Y = destRect.Y + destRect.Height;
                ptr[3].Position.Z = 0;
                ptr[3].Normal.X = 0;
                ptr[3].Normal.Y = 0;
                ptr[3].Normal.Z = 1;
                ptr[3].TextureCoordinate.X = 1;
                ptr[3].TextureCoordinate.Y = 1;
                ptr[3].TextureCoordinate.Z = 0;
                ptr[0].Hue = ptr[1].Hue = ptr[2].Hue = ptr[3].Hue = hue;
            }

            return DrawSprite(texture, _vertexBufferUI, Techniques.Hued);
        }

        public bool Draw2DTiled(Texture2D texture, Rectangle destRect, Vector3 hue)
        {
            /* float drawCountX = destRect.Width / (float)texture.Width;
            float drawCountY = destRect.Height / (float)texture.Height;

            fixed (SpriteVertex* ptr = _vertexBufferUI)
            {
                ptr[0].Position.X = destRect.X;
                ptr[0].Position.Y = destRect.Y;
                ptr[0].Position.Z = 0;
                ptr[0].Normal.X = 0;
                ptr[0].Normal.Y = 0;
                ptr[0].Normal.Z = 1;
                ptr[0].TextureCoordinate = Vector3.Zero;

                ptr[1].Position.X = destRect.Right;
                ptr[1].Position.Y = destRect.Y;
                ptr[1].Position.Z = 0;
                ptr[1].Normal.X = 0;
                ptr[1].Normal.Y = 0;
                ptr[1].Normal.Z = 1;
                ptr[1].TextureCoordinate.X = drawCountX;
                ptr[1].TextureCoordinate.Y = 0;
                ptr[1].TextureCoordinate.Z = 0;

                ptr[2].Position.X = destRect.X;
                ptr[2].Position.Y = destRect.Bottom;
                ptr[2].Position.Z = 0;
                ptr[2].Normal.X = 0;
                ptr[2].Normal.Y = 0;
                ptr[2].Normal.Z = 1;
                ptr[2].TextureCoordinate.X = 0;
                ptr[2].TextureCoordinate.Y = drawCountY;
                ptr[2].TextureCoordinate.Z = 0;

                ptr[3].Position.X = destRect.Right;
                ptr[3].Position.Y = destRect.Bottom;
                ptr[3].Position.Z = 0;
                ptr[3].Normal.X = 0;
                ptr[3].Normal.Y = 0;
                ptr[3].Normal.Z = 1;
                ptr[3].TextureCoordinate.X = drawCountX;
                ptr[3].TextureCoordinate.Y = drawCountY;
                ptr[3].TextureCoordinate.Z = 0;
                ptr[0].Hue = ptr[1].Hue = ptr[2].Hue = ptr[3].Hue = hue;
            }

            return DrawSprite(texture, _vertexBufferUI); */
            int y = destRect.Y;
            int h = destRect.Height;

            while (h > 0)
            {
                int x = destRect.X;
                int w = destRect.Width;
                Rectangle sRect = new Rectangle(0, 0, texture.Width, h < texture.Height ? h : texture.Height);

                while (w > 0)
                {
                    if (w < texture.Width)
                        sRect.Width = w;
                    Draw2D(texture, new Point(x, y), sRect, hue);
                    w -= texture.Width;
                    x += texture.Width;
                }

                h -= texture.Height;
                y += texture.Height;
            }

            return true;
        }

        public bool DrawRectangle(Texture2D texture, Rectangle rectangle, Vector3 hue)
        {
            Draw2D(texture, new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, 1), hue);
            Draw2D(texture, new Rectangle(rectangle.Right, rectangle.Y, 1, rectangle.Height), hue);
            Draw2D(texture, new Rectangle(rectangle.X, rectangle.Bottom, rectangle.Width, 1), hue);
            Draw2D(texture, new Rectangle(rectangle.X, rectangle.Y, 1, rectangle.Height), hue);
            return true;
        }


        [Conditional("DEBUG")]
        private void EnsureStarted()
        {
            if (!_started)
                throw new InvalidOperationException();
        }

        [Conditional("DEBUG")]
        private void EnsureNotStarted()
        {
            if (_started)
                throw new InvalidOperationException();
        }

        private void ApplyStates()
        {
            GraphicsDevice.BlendState = _blendState;
            GraphicsDevice.DepthStencilState = _stencil;
            GraphicsDevice.RasterizerState = _useScissor ? _rasterizerState : RasterizerState.CullNone;
            GraphicsDevice.SamplerStates[0] = SamplerState.PointClamp;

            GraphicsDevice.SamplerStates[1] = SamplerState.PointClamp;
            GraphicsDevice.SamplerStates[2] = SamplerState.PointClamp;

            Viewport viewport = GraphicsDevice.Viewport;
            _projectionMatrix.M11 = (float)(2.0 / viewport.Width);
            _projectionMatrix.M22 = (float)(-2.0 / viewport.Height);
            //_projectionMatrix.M41 = -1 - 0.5f * _projectionMatrix.M11;
            //_projectionMatrix.M42 = 1 - 0.5f * _projectionMatrix.M22;
            Matrix.Multiply(ref _transformMatrix, ref _projectionMatrix, out _matrixTransformMatrix);

            _projectionMatrixEffect.SetValue(_matrixTransformMatrix);
            _worldMatrixEffect.SetValue(_transformMatrix);

            _viewportEffect.SetValue(new Vector2(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height));
            GraphicsDevice.SetVertexBuffer(_vertexBuffer);
            GraphicsDevice.Indices = _indexBuffer;
        }

        private unsafe void Flush()
        {
            ApplyStates();

            if (_numSprites == 0)
                return;

            fixed (SpriteVertex* p = &_vertexInfo[0]) _vertexBuffer.SetDataPointerEXT(0, (IntPtr)p, _numSprites * 4 * SpriteVertex.SizeInBytes, SetDataOptions.None);
            Texture2D current = _textureInfo[0];
            int offset = 0;

            _effect.CurrentTechnique.Passes[0].Apply();

            for (int i = 1; i < _numSprites; i++)
            {
                if (_textureInfo[i] != current)
                {
                    InternalDraw(current, offset, i - offset);
                    current = _textureInfo[i];
                    offset = i;
                }
            }

            InternalDraw(current, offset, _numSprites - offset);
            //Array.Clear(_textureInfo, 0, _numSprites);
            _numSprites = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InternalDraw(Texture2D texture, int baseSprite, int batchSize)
        {
            GraphicsDevice.Textures[0] = texture;
            GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, baseSprite * 4, 0, batchSize * 4, 0, batchSize * 2);
        }

        public void EnableScissorTest(bool enable)
        {
            if (enable == _useScissor)
                return;

            Flush();

            //_rasterizerState?.Dispose();

            _useScissor = enable;
        }

        private bool _useScissor;


        public void SetBlendState(BlendState blend, bool noflush = false)
        {
            if (!noflush)
                Flush();

            _blendState = blend ?? BlendState.AlphaBlend;
        }


        public void SetStencil(DepthStencilState stencil, bool noflush = false)
        {
            if (!noflush)
                Flush();

            _stencil = stencil ?? DepthStencilState.None;
        }

        private static short[] GenerateIndexArray()
        {
            short[] result = new short[MAX_INDICES];

            for (int i = 0, j = 0; i < MAX_INDICES; i += 6, j += 4)
            {
                result[i] = (short)j;
                result[i + 1] = (short)(j + 1);
                result[i + 2] = (short)(j + 2);
                result[i + 3] = (short)(j + 1);
                result[i + 4] = (short)(j + 3);
                result[i + 5] = (short)(j + 2);
            }

            return result;
        }
    }
}
