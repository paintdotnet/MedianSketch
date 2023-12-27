using PaintDotNet;
using PaintDotNet.Direct2D1;
using PaintDotNet.Direct2D1.Effects;
using PaintDotNet.Rendering;
using System;
using System.Collections.Generic;

namespace MedianSketch;

internal sealed class MedianApproximationEffect
    : CustomEffect<MedianApproximationEffect.Props>
{
    // NOTE: This can be dangerous. For example, if set to 200 along with a high sample count, it
    //       can TDR or even reboot your system (ask me how I know).
    internal const float MaxRadius = 50;

    public MedianApproximationEffect(IDeviceEffectFactory factory)
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

        public EffectPropertyAccessor<float> Radius => CreateFloatPropertyAccessor(0);

        public EffectPropertyAccessor<float> Percentile => CreateFloatPropertyAccessor(1);

        public EffectPropertyAccessor<int> Iterations => CreateInt32PropertyAccessor(2);

        public EffectPropertyAccessor<float> SampleCountPercent => CreateFloatPropertyAccessor(3); // [0,1]

        public EffectPropertyAccessor<BorderEdgeMode2> EdgeMode => CreateEnumPropertyAccessor<BorderEdgeMode2>(4);

        public EffectPropertyAccessor<uint> RandomSeed => CreateUInt32PropertyAccessor(5);

        public EffectPropertyAccessor<uint> RandomOffset => CreateUInt32PropertyAccessor(6);
    }

    private sealed class Impl
        : CustomEffectImpl<Props>
    {
        private BorderEffect2? borderEffect;
        private ITransformNode? borderTransform;

        private List<P2QuantileEstimatorEffect?> p2QuantileEstimatorEffects = new();
        private List<ITransformNode?> p2QuantileEstimatorTransforms = new();

        private CompositeEffect? compositeEffect;
        private ITransformNode? compositeTransform;

        private HlslBinaryOperatorEffect? divideEffect;
        private ITransformNode? divideTransform;

        public Impl()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (P2QuantileEstimatorEffect? p2QuantileEstimatorEffect in this.p2QuantileEstimatorEffects)
                {
                    p2QuantileEstimatorEffect?.Dispose();
                }

                foreach (ITransformNode? p2QuantileEstimatorTransform in this.p2QuantileEstimatorTransforms)
                {
                    p2QuantileEstimatorTransform?.Dispose();
                }
            }

            DisposableUtil.Free(ref this.borderEffect, disposing);
            DisposableUtil.Free(ref this.borderTransform, disposing);
            this.p2QuantileEstimatorEffects.Clear();
            this.p2QuantileEstimatorTransforms.Clear();
            DisposableUtil.Free(ref this.compositeEffect, disposing);
            DisposableUtil.Free(ref this.compositeTransform, disposing);
            DisposableUtil.Free(ref this.divideEffect, disposing);
            DisposableUtil.Free(ref this.divideTransform, disposing);

            base.Dispose(disposing);
        }

        protected override void OnInitialize()
        {
            this.Properties.Radius.SetValue(15.0f);
            this.Properties.Percentile.SetValue(0.5f);
            this.Properties.Iterations.SetValue(2);
            this.Properties.SampleCountPercent.SetValue(0.10f);
            this.Properties.EdgeMode.SetValue(BorderEdgeMode2.Transparent);
            this.Properties.RandomSeed.SetValue(0);
            this.Properties.RandomOffset.SetValue(0);
            
            this.borderEffect = new BorderEffect2(this.EffectContext);

            this.compositeEffect = new CompositeEffect(this.EffectContext);
            this.compositeEffect.Properties.Mode.SetValue(CompositeMode.Plus);

            this.divideEffect = new HlslBinaryOperatorEffect(this.EffectContext);
            this.divideEffect.Properties.Operator.SetValue(HlslBinaryOperator.Multiply);
            this.divideEffect.Properties.Parameter1.SetValue(HlslEffectParameter.Input);
            this.divideEffect.Properties.Parameter2.SetValue(HlslEffectParameter.Value);

            this.borderTransform = this.EffectContext.CreateTransformNodeFromEffect(this.borderEffect);
            this.compositeTransform = this.EffectContext.CreateTransformNodeFromEffect(this.compositeEffect);
            this.divideTransform = this.EffectContext.CreateTransformNodeFromEffect(this.divideEffect);

            base.OnInitialize();
        }

        protected override void OnPrepareForRender(ChangeType changeType)
        {
            float radius = Math.Clamp(this.Properties.Radius.GetValue(), 0.0f, MaxRadius);
            float percentile = Math.Clamp(this.Properties.Percentile.GetValue(), 0, 1);
            int iterations = Math.Clamp(this.Properties.Iterations.GetValue(), 1, int.MaxValue);
            float sampleCountPercent = Math.Clamp(this.Properties.SampleCountPercent.GetValue(), 0.0001f, 1.0f);
            BorderEdgeMode2 edgeMode = this.Properties.EdgeMode.GetValue();
            uint randomSeed = this.Properties.RandomSeed.GetValue();
            uint randomOffset = this.Properties.RandomOffset.GetValue();

            this.TransformGraph.Clear();

            if (radius == 0)
            {
                this.TransformGraph.SetPassthroughGraph(0);
            }
            else
            {
                // [input] -> [border] -> [[P2QuantileEstimatorEffects]] -> [composite] -> [divide]

                this.TransformGraph.AddNode(this.borderTransform!);
                this.TransformGraph.ConnectToEffectInput(0, this.borderTransform!, 0);
                this.borderEffect!.Properties.EdgeMode.SetValue(edgeMode);

                this.TransformGraph.AddNode(this.compositeTransform!);

                for (int i = this.p2QuantileEstimatorEffects.Count - 1; i >= iterations; --i)
                {
                    this.p2QuantileEstimatorEffects[i]?.Dispose();
                    this.p2QuantileEstimatorEffects.RemoveAt(i);

                    this.p2QuantileEstimatorTransforms[i]?.Dispose();
                    this.p2QuantileEstimatorTransforms.RemoveAt(i);
                }

                while (this.p2QuantileEstimatorEffects.Count < iterations)
                {
                    this.p2QuantileEstimatorEffects.Add(null);
                    this.p2QuantileEstimatorTransforms.Add(null);
                }

                this.compositeEffect!.InputCount = iterations;

                const int minSampleCount = 5;
                double maxSampleCount0 = Math.PI * (radius * radius);
                int maxSampleCount1 = (int)Math.Round(maxSampleCount0);
                int maxSampleCount = Math.Max(minSampleCount, maxSampleCount1);

                for (int i = 0; i < iterations; ++i)
                {
                    Random random = new Random(unchecked((int)randomSeed + i));
                    float nextFloat()
                    {
                        return (float)((random.Next(1 << 30) / (double)(1 << 29)) - 1.0);
                    }

                    double keptSampleCount0 = maxSampleCount * sampleCountPercent;
                    int keptSampleCount1 = (int)Math.Round(keptSampleCount0);
                    int keptSampleCount = Math.Clamp(keptSampleCount1, minSampleCount, maxSampleCount);

                    List<Vector2Float> sampleOffsets = new();
                    for (int s = 0; s < keptSampleCount; ++s)
                    {
                        sampleOffsets.Add(default);
                    }

                    for (int s = 0; s < maxSampleCount; ++s)
                    {
                        Vector2Float sampleOffsetNorm = new Vector2Float(float.MaxValue, float.MaxValue);
                        while (sampleOffsetNorm.LengthSquared > 1)
                        {
                            sampleOffsetNorm = new Vector2Float(nextFloat(), nextFloat());
                        }

                        if (s < keptSampleCount)
                        {
                            Vector2Float sampleOffset = sampleOffsetNorm * radius;
                            sampleOffsets[(int)((s + randomOffset) % keptSampleCount)] = sampleOffset;
                        }
                    }

                    if (this.p2QuantileEstimatorEffects[i] == null)
                    {
                        this.p2QuantileEstimatorEffects[i] = new P2QuantileEstimatorEffect(this.EffectContext);
                        this.p2QuantileEstimatorTransforms[i] = this.EffectContext.CreateTransformNodeFromEffect(this.p2QuantileEstimatorEffects[i]!);
                    }

                    this.p2QuantileEstimatorEffects[i]!.Properties.SamplingOffsets.SetValue(sampleOffsets);
                    this.p2QuantileEstimatorEffects[i]!.Properties.Percentile.SetValue(percentile);

                    this.TransformGraph.AddNode(this.p2QuantileEstimatorTransforms[i]!);
                    this.TransformGraph.ConnectNode(this.borderTransform!, this.p2QuantileEstimatorTransforms[i]!, 0);
                    this.TransformGraph.ConnectNode(this.p2QuantileEstimatorTransforms[i]!, this.compositeTransform!, i);
                }

                if (this.p2QuantileEstimatorTransforms.Count == 1)
                {
                    // No need for composite or division when there's only 1
                    this.TransformGraph.SetOutputNode(this.p2QuantileEstimatorTransforms[0]!);
                }
                else
                {
                    this.TransformGraph.AddNode(this.divideTransform!);
                    this.TransformGraph.ConnectNode(this.compositeTransform!, this.divideTransform!, 0);
                    this.divideEffect!.Properties.Value2.SetValue(new Vector4Float((float)(1.0 / this.p2QuantileEstimatorEffects.Count)));

                    this.TransformGraph.SetOutputNode(this.divideTransform!);
                }
            }

            base.OnPrepareForRender(changeType);
        }
    }
}
