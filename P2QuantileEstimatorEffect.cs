using CommunityToolkit.HighPerformance;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Interop;
using PaintDotNet.Collections;
using PaintDotNet.ComponentModel;
using PaintDotNet.Direct2D1;
using PaintDotNet.Direct2D1.Effects;
using PaintDotNet.Interop;
using PaintDotNet.Rendering;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MedianSketch;

internal sealed partial class P2QuantileEstimatorEffect
    : CustomEffect<P2QuantileEstimatorEffect.Props>
{
    public P2QuantileEstimatorEffect(IDeviceEffectFactory factory)
        : base(factory)
    {
    }

    public sealed class Props
        : CustomEffectProperties
    {
        protected override CustomEffectImpl CreateImpl()
        {
            return new Impl();
        }

        public EffectInputAccessor Input => CreateInputAccessor(0);

        public EffectPropertyAccessor<IReadOnlyList<Vector2Float>> SamplingOffsets => CreateReadOnlyListAsBlobPropertyAccessor<Vector2Float>(0);

        public EffectPropertyAccessor<float> Percentile => CreateFloatPropertyAccessor(1);
    }

    private sealed partial class Impl
        : CustomEffectImpl<Props>
    {
        public Impl()
        {
        }

        protected override void OnInitialize()
        {
            // Set default values for properties
            this.Properties.SamplingOffsets.SetValue(ReadOnlySpan<Vector2Float>.Empty);
            this.Properties.Percentile.SetValue(0.5f);

            base.OnInitialize();
        }

        protected override unsafe void OnPrepareForRender(ChangeType changeType)
        {
            IReadOnlyList<Vector2Float> samplingOffsets = this.Properties.SamplingOffsets.GetValue();
            float percentile = this.Properties.Percentile.GetValue();

            this.TransformGraph.Clear();

            // TODO: need to handle counts of 1-4, with separate shaders. 1 can't be passthrough unless the sampling offset is [0,0]
            if (samplingOffsets.Count < 5)
            {
                this.TransformGraph.SetPassthroughGraph(0);
            }
            else
            {
                float samplingLeft = 0;
                float samplingTop = 0;
                float samplingRight = 0;
                float samplingBottom = 0;
                foreach (Vector2Float samplingOffset in samplingOffsets)
                {
                    samplingLeft = Math.Min(samplingLeft, samplingOffset.X);
                    samplingTop = Math.Min(samplingTop, samplingOffset.Y);
                    samplingRight = Math.Max(samplingRight, samplingOffset.X);
                    samplingBottom = Math.Max(samplingBottom, samplingOffset.Y);
                }

                RectFloat samplingRect = RectFloat.FromEdges(samplingLeft, samplingTop, samplingRight, samplingBottom);
                RectInt32 samplingRectI = samplingRect.Int32Bound;

                Vector4Float[] samplingOffsets4 = samplingOffsets
                    .Select(v => new Vector4Float(v, Vector2Float.Zero))
                    .ToArray();

                using IDirect2DFactory d2dFactory = this.EffectContext.GetFactory();
                d2dFactory.RegisterEffectFromBlob(D2D1PixelShaderEffect.GetRegistrationBlob<P2QuantileEstimatorShader>(out Guid shaderID));

                using IDeviceEffect shaderEffect = this.EffectContext.CreateEffect(shaderID);

                ReadOnlySpan<uint> resTexExtents = stackalloc uint[1] { (uint)samplingOffsets4.Length };
                ReadOnlySpan<D2D1ExtendMode> resTexExtendModes = stackalloc D2D1ExtendMode[1] { D2D1ExtendMode.Clamp };
                D2D1ResourceTextureManager resourceTexture = new D2D1ResourceTextureManager(
                    resTexExtents,
                    D2D1BufferPrecision.Float32,
                    D2D1ChannelDepth.Four,
                    D2D1Filter.MinMagMipPoint,
                    resTexExtendModes,
                    samplingOffsets4.AsSpan().AsBytes(),
                    default);

                P2QuantileEstimatorTransformMapper transformMapper = new P2QuantileEstimatorTransformMapper(samplingRectI);
                using IObjectRef transformMapperRef = transformMapper.CreateObjectRef();
                shaderEffect.SetValueRef(D2D1PixelShaderEffectProperty.TransformMapper, transformMapperRef);

                using IObjectRef resourceTextureRef = resourceTexture.CreateObjectRef();
                shaderEffect.SetValueRef(D2D1PixelShaderEffectProperty.ResourceTextureManager1, resourceTextureRef);

                P2QuantileEstimatorShader shader = new P2QuantileEstimatorShader(
                    percentile,
                    percentile / 2.0f,
                    (percentile + 1.0f) / 2.0f,
                    percentile >= 0.5f,
                    Unsafe.As<RectInt32, int4>(ref samplingRectI));

                shaderEffect.SetValue(
                    D2D1PixelShaderEffectProperty.ConstantBuffer,
                    D2D1PixelShader.GetConstantBuffer(shader));

                using ITransformNode shaderTransform = this.EffectContext.CreateTransformNodeFromEffect(shaderEffect);
                this.TransformGraph.AddNode(shaderTransform);
                this.TransformGraph.ConnectToEffectInput(0, shaderTransform, 0);
                this.TransformGraph.SetOutputNode(shaderTransform);
            }

            base.OnPrepareForRender(changeType);
        }

        [D2DInputCount(1)]
        [D2DInputSimple(0)]
        [D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
        [AutoConstructor]
        private readonly partial struct P2QuantileEstimatorShader
            : ID2D1PixelShader
        {
            private readonly float p;
            private readonly float pDiv2;       // p/2
            private readonly float pPlus1Div2;  // (p+1)/2
            private readonly Bool pGTE0pt5;     // p >= 0.5f
            private readonly int4 samplingXYWH;

            [D2DResourceTextureIndex(1)]
            private readonly D2D1ResourceTexture1D<float4> offsets;

            public float4 Execute()
            {
                HlslP2QuantileEstimator p2Estimator = new HlslP2QuantileEstimator();
                p2Estimator.SetProbability(this.p, this.pDiv2, this.pPlus1Div2, this.pGTE0pt5);

                float4 sample0 = D2D.SampleInputAtOffset(0, this.offsets[0].XY);
                float4 sample1 = D2D.SampleInputAtOffset(0, this.offsets[1].XY);
                float4 sample2 = D2D.SampleInputAtOffset(0, this.offsets[2].XY);
                float4 sample3 = D2D.SampleInputAtOffset(0, this.offsets[3].XY);
                float4 sample4 = D2D.SampleInputAtOffset(0, this.offsets[4].XY);
                p2Estimator.AddFirst5Values(sample0, sample1, sample2, sample3, sample4);

                int offsetsLength = this.offsets.Width;
                for (int i = 5; i < offsetsLength; ++i)
                {
                    float2 offset = this.offsets[i].XY;
                    float4 sample = D2D.SampleInputAtOffset(0, offset);
                    p2Estimator.AddValueAfter5th(sample);
                }

                return p2Estimator.GetQuantile();
            }
        }

        private sealed class P2QuantileEstimatorTransformMapper
            : D2D1TransformMapper<P2QuantileEstimatorShader>
        {
            private readonly RectInt32 samplingRect;

            public P2QuantileEstimatorTransformMapper(RectInt32 samplingRect)
            {
                this.samplingRect = samplingRect;
            }

            public override void MapInputsToOutput(
                D2D1DrawInfoUpdateContext<P2QuantileEstimatorShader> drawInfoUpdateContext, 
                ReadOnlySpan<Rectangle> inputs, 
                ReadOnlySpan<Rectangle> opaqueInputs, 
                out Rectangle output, 
                out Rectangle opaqueOutput)
            {
                if (inputs.Length != 1)
                {
                    throw new IndexOutOfRangeException();
                }

                MapInvalidOutput(0, inputs[0], out output);
                opaqueOutput = default;
            }

            public override void MapOutputToInputs(in Rectangle output, Span<Rectangle> inputs)
            {
                if (inputs.Length != 1)
                {
                    throw new IndexOutOfRangeException();
                }

                MapInvalidOutput(0, output, out inputs[0]);
            }

            public override void MapInvalidOutput(int inputIndex, in Rectangle invalidInput, out Rectangle invalidOutput)
            {
                if (inputIndex != 0)
                {
                    throw new IndexOutOfRangeException();
                }

                RectInt64 rect0 = RectInt64.Offset(invalidInput, this.samplingRect.Location);
                RectInt64 rect1 = new RectInt64(rect0.Location, rect0.Width + this.samplingRect.Width, rect0.Height + this.samplingRect.Height);
                RectInt64 rect2 = RectInt64.Intersect(rect1, RectInt32.LogicallyInfinite);
                invalidOutput = (RectInt32)rect2;
            }
        }
    }
}
