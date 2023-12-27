using PaintDotNet;
using PaintDotNet.Direct2D1;
using PaintDotNet.Direct2D1.Effects;
using PaintDotNet.Effects;
using PaintDotNet.Effects.Gpu;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;
using System;
using System.Collections.Generic;

namespace MedianSketch;

internal sealed partial class MedianSketchGpuEffect
    : PropertyBasedGpuImageEffect
{
    public MedianSketchGpuEffect()
        : base(
            "Median Sketch",
            SubmenuNames.Artistic,
            GpuImageEffectOptions.Create() with
            {
                IsConfigurable = true
            })
    {
    }

    private enum PropertyNames
    {
        Radius,
        Percentile,
        Iterations
    }

    protected override PropertyCollection OnCreatePropertyCollection()
    {
        List<Property> props = new List<Property>();

        props.Add(new DoubleProperty(PropertyNames.Radius, 25, 0, MedianApproximationEffect.MaxRadius)); 
        props.Add(new Int32Property(PropertyNames.Percentile, 50, 0, 100));
        props.Add(new Int32Property(PropertyNames.Iterations, 3, 1, 20));

        return new PropertyCollection(props);
    }

    protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
    {
        ControlInfo configUI = CreateDefaultConfigUI(props);

        configUI.SetPropertyControlValue(PropertyNames.Radius, ControlInfoPropertyNames.DisplayName, "Radius");
        configUI.SetPropertyControlValue(PropertyNames.Radius, ControlInfoPropertyNames.ShowHeaderLine, false);

        configUI.SetPropertyControlValue(PropertyNames.Percentile, ControlInfoPropertyNames.DisplayName, "Percentile");
        configUI.SetPropertyControlValue(PropertyNames.Percentile, ControlInfoPropertyNames.ShowHeaderLine, false);

        configUI.SetPropertyControlValue(PropertyNames.Iterations, ControlInfoPropertyNames.DisplayName, "Iterations");
        configUI.SetPropertyControlValue(PropertyNames.Iterations, ControlInfoPropertyNames.ShowHeaderLine, false);

        return configUI;
    }

    protected override void OnInitializeRenderInfo(IGpuImageEffectRenderInfo renderInfo)
    {
        renderInfo.InputAlphaMode = GpuEffectAlphaMode.Straight;
        renderInfo.OutputAlphaMode = GpuEffectAlphaMode.Straight;
        base.OnInitializeRenderInfo(renderInfo);
    }

    private MedianApproximationEffect? medianEffect;

    protected override void OnInvalidateDeviceResources()
    {
        DisposableUtil.Free(ref this.medianEffect);
        base.OnInvalidateDeviceResources();
    }

    protected override IDeviceImage OnCreateOutput(IDeviceContext deviceContext)
    {
        this.medianEffect = new MedianApproximationEffect(deviceContext);
        this.medianEffect.Properties.Input.Set(this.Environment.SourceImage);
        this.medianEffect.Properties.SampleCountPercent.SetValue(0.01f);
        this.medianEffect.Properties.EdgeMode.SetValue(BorderEdgeMode2.Mirror);

        return this.medianEffect;
    }

    protected override InspectTokenAction OnInspectTokenChanges(PropertyBasedEffectConfigToken oldToken, PropertyBasedEffectConfigToken newToken)
    {
        return InspectTokenAction.UpdateOutput;
    }

    protected override unsafe void OnUpdateOutput(IDeviceContext deviceContext)
    {
        double radius = this.Token!.GetProperty<DoubleProperty>(PropertyNames.Radius)!.Value;
        int percentile = this.Token!.GetProperty<Int32Property>(PropertyNames.Percentile)!.Value;
        int iterations = this.Token!.GetProperty<Int32Property>(PropertyNames.Iterations)!.Value;

        this.medianEffect!.Properties.Radius.SetValue((float)radius);
        this.medianEffect!.Properties.Percentile.SetValue(percentile / 100.0f);
        this.medianEffect!.Properties.Iterations.SetValue(iterations);

        base.OnUpdateOutput(deviceContext);
    }
}
