using ComputeSharp;
using System;

namespace MedianSketch;

// Adapted from:
// https://aakinshin.net/posts/p2-quantile-estimator/
// https://aakinshin.net/posts/p2-quantile-estimator-rounding-issue/
// https://aakinshin.net/posts/p2-quantile-estimator-initialization/
// https://aakinshin.net/posts/p2-quantile-estimator-adjusting-order/
internal struct HlslP2QuantileEstimator
{
    private float p;
    private float pDiv2;
    private float pPlus1Div2;
    private Bool pGTE0pt5;

    private int count;

    // Marker positions
    private int4 n0;
    private int4 n1;
    private int4 n2;
    private int4 n3;
    private int4 n4;

    // Desired marker position
    private float4 ns0;
    private float4 ns1;
    private float4 ns2;
    private float4 ns3;
    private float4 ns4;

    // Marker heights
    private float4 q0;
    private float4 q1;
    private float4 q2;
    private float4 q3;
    private float4 q4;

    public HlslP2QuantileEstimator()
    {
    }

    // NOTE: Bug in CS.D2D1 v2.x means that these parameters can't use the same name as the fields.
    //       Fixed in 3.0: https://github.com/Sergio0694/ComputeSharp/issues/726
    public void SetProbability(float p_, float pDiv2_, float pPlus1Div2_, Bool pGTE0pt5_)
    {
        this.p = p_;
        this.pDiv2 = pDiv2_;
        this.pPlus1Div2 = pPlus1Div2_;
        this.pGTE0pt5 = pGTE0pt5_;
    }

    public float4 GetQuantile()
    {
        // NOTE: We must always have count>=5 !
        // This is fine because r=1 has 5 values, and r=0 can just use passthrough rendering.
        return this.q2;
    }

    public void AddFirst5Values(float4 x0, float4 x1, float4 x2, float4 x3, float4 x4)
    {
        this.q0 = x0;
        this.q1 = x1;
        this.q2 = x2;
        this.q3 = x3;
        this.q4 = x4;

        this.count = 5;

        AfterFirst5Values();
    }

    private void AfterFirst5Values()
    {
        SortQ();

        this.ns0 = this.q0;
        this.ns1 = this.q1;
        this.ns2 = this.q2;
        this.ns3 = this.q3;
        this.ns4 = this.q4;

        this.n0 = 0;
        this.n1 = (int)Hlsl.Round(2 * this.p);
        this.n2 = (int)Hlsl.Round(4 * this.p);
        this.n3 = (int)Hlsl.Round(2 + 2 * this.p);
        this.n4 = 4;

        this.q1 = GatherNS(this.n1);
        this.q2 = GatherNS(this.n2);
        this.q3 = GatherNS(this.n3);

        this.ns0 = 0;
        this.ns1 = 2 * this.p;
        this.ns2 = 4 * this.p;
        this.ns3 = 2 + 2 * this.p;
        this.ns4 = 4;
    }

    private void SortQ()
    {
        Sort5(ref this.q0, ref this.q1, ref this.q2, ref this.q3, ref this.q4);
    }

    // NOTE: This doesn't really sort the vectors, it sorts the values _across_ the vectors
    //       So, sorting [0,0,0,1] and [0,0,1,0] would result in [0,0,0,0] and [0,0,1,1]
    //       This is fine because we are operating on a per-channel basis. If we wanted to
    //       sort them together, we'd probably sort based on intensity.
    private static void Sort5(ref float4 q0, ref float4 q1, ref float4 q2, ref float4 q3, ref float4 q4)
    {
        // Pull the max value into q4
        Sort(ref q0, ref q4);
        Sort(ref q1, ref q4);
        Sort(ref q2, ref q4);
        Sort(ref q3, ref q4);

        // Pull the next largest value into q3
        Sort(ref q0, ref q3);
        Sort(ref q1, ref q3);
        Sort(ref q2, ref q3);

        // Pull the next largest value into q2
        Sort(ref q0, ref q2);
        Sort(ref q1, ref q2);

        // etc.
        Sort(ref q0, ref q1);
    }

    private static void Sort(ref float4 a, ref float4 b)
    {
        float4 a2 = a;
        float4 b2 = b;
        a = Hlsl.Min(a2, b2);
        b = Hlsl.Max(a2, b2);
    }

    private float4 GatherNS(int4 index)
    {
        // aka return new float4(this.ns[index[0]], ..., this.ns[index[3]])

        // This should be rewritten to use ?:, aka Hlsl.Select(), when upgraded to ComputeSharp 3.0
        // This only runs once per pixel so it doesn't affect performance much; proper optimization
        // effort should be focused on AddValueAfter5th() and its call graph.
        int4 iPow2 = int4.One << index;
        return Hlsl.AsFloat(
               Hlsl.AsUInt(this.ns0) & LowBitAsBoolToMask(iPow2)
             | Hlsl.AsUInt(this.ns1) & LowBitAsBoolToMask(iPow2 >> new int4(1, 1, 1, 1))
             | Hlsl.AsUInt(this.ns2) & LowBitAsBoolToMask(iPow2 >> new int4(2, 2, 2, 2))
             | Hlsl.AsUInt(this.ns3) & LowBitAsBoolToMask(iPow2 >> new int4(3, 3, 3, 3))
             | Hlsl.AsUInt(this.ns4) & LowBitAsBoolToMask(iPow2 >> new int4(4, 4, 4, 4)));
    }

    private static uint4 LowBitAsBoolToMask(int4 selector)
    {
        return ~((Hlsl.AsUInt(selector) & uint4.One) - 1);
    }

    public void AddValueAfter5th(float4 x)
    {
        bool4 xLTq0Bit = x < this.q0;
        bool4 xLTq1Bit = x < this.q1;
        bool4 xLTq2Bit = x < this.q2;
        bool4 xLTq3Bit = x < this.q3;
        bool4 xLTq4Bit = x < this.q4;
        bool4 elseBit = !(xLTq0Bit | xLTq1Bit | xLTq2Bit | xLTq3Bit | xLTq4Bit);

        bool4 writeQ0Bit = xLTq0Bit;
        bool4 incN1Bit = xLTq0Bit | xLTq1Bit;
        bool4 incN2Bit = incN1Bit | xLTq2Bit;
        bool4 incN3Bit = incN2Bit | xLTq3Bit;
        bool4 writeQ4Bit = elseBit;

        this.q0 = Select(writeQ0Bit, x, this.q0);
        this.n1 += Hlsl.BoolToInt(incN1Bit);
        this.n2 += Hlsl.BoolToInt(incN2Bit);
        this.n3 += Hlsl.BoolToInt(incN3Bit);
        this.n4 += 1;
        this.q4 = Select(writeQ4Bit, x, this.q4);

        this.ns1 = this.count * this.pDiv2;
        this.ns2 = this.count * this.p;
        this.ns3 = this.count * this.pPlus1Div2;
        this.ns4 = this.count;

        if (this.pGTE0pt5)
        {
            AdjustPGTE0pt5();
        }
        else
        {
            AdjustPLT0pt5();
        }

        ++this.count;
    }

    private void AdjustPLT0pt5()
    {
        Adjust(out this.q3, out this.n3, this.n2, this.n3, this.n4, this.ns3, this.q2, this.q3, this.q4);
        Adjust(out this.q2, out this.n2, this.n1, this.n2, this.n3, this.ns2, this.q1, this.q2, this.q3);
        Adjust(out this.q1, out this.n1, this.n0, this.n1, this.n2, this.ns1, this.q0, this.q1, this.q2);
    }

    private void AdjustPGTE0pt5()
    {
        Adjust(out this.q1, out this.n1, this.n0, this.n1, this.n2, this.ns1, this.q0, this.q1, this.q2);
        Adjust(out this.q2, out this.n2, this.n1, this.n2, this.n3, this.ns2, this.q1, this.q2, this.q3);
        Adjust(out this.q3, out this.n3, this.n2, this.n3, this.n4, this.ns3, this.q2, this.q3, this.q4);
    }

    private void Adjust(out float4 qOut, out int4 nOut, int4 niM1, int4 ni, int4 niP1, float4 nsi, float4 qiM1, float4 qi, float4 qiP1)
    {
        float4 d = nsi - ni;
        int4 ds = Hlsl.Sign(d); // -1, 0, +1

        bool4 adjustA1 = d >= float4.One;
        bool4 adjustA2 = niP1 - ni > int4.One;
        bool4 adjustB1 = d <= -float4.One;
        bool4 adjustB2 = niM1 - ni < -int4.One;
        bool4 adjust = (adjustA1 & adjustA2) | (adjustB1 & adjustB2);

        // I typo'd this code such that it selected q[i],q[i+1],q[i] instead of q[i-1],q[i],q[i+1]. 
        // But it doesn't seem to affect the output. And since ds equaling 0 is unlikely(?) because it's
        // calculated from two floating point values, we can just use qi. This saves a lot of performance!
        // This is the wrong/typo'd code:
        //     float4 qiPd = Select(Hlsl.IntToBool(ds), qi, Select((ds + 1) >= int4.Zero, qiP1, qiM1));
        // This is the correct code:
        //     float4 qiPd = Select(ds < int4.Zero, qiM1, Select(ds == int4.Zero, qi, qiP1));
        // This is the "optimized" typo'd code that doesn't seem to affect the output and is obviously faster:
        float4 qiPds = qi; // q[i + ds]

        int4 niPds = Select(ds == int4.Zero, ni, Select(ds > int4.Zero, niP1, niM1)); // n[i + ds]
        float4 ql = Linear(ds, qi, qiPds, ni, niPds);
        float4 qp = Parabolic(ds, qiM1, qi, qiP1, niM1, ni, niP1);
        float4 qOld = qi;
        float4 qNew = Select((qiM1 < qp) & (qp < qiP1), qp, ql);
        qOut = Select(adjust, qNew, qOld);

        nOut = ni + (Hlsl.BoolToInt(adjust) * ds);
    }

    private static float4 Parabolic(int4 ds, float4 qiM1, float4 qi, float4 qiP1, int4 niM1, int4 ni, int4 niP1)
    {
        return qi + ds / (float4)(niP1 - niM1) * (
            (ni - niM1 + ds) * (qiP1 - qi) / (niP1 - ni) +
            (niP1 - ni - ds) * (qi - qiM1) / (ni - niM1));
    }

    private static float4 Linear(int4 ds, float4 qi, float4 qiPd, int4 ni, int4 niPd)
    {
        return qi + ds * (qiPd - qi) / (niPd - ni);
    }

    // We can't just multiply by 0f and 1f because (0*nan)==nan, which then breaks everything. So we use masking.
    // TODO: Use ?: ternary operator, aka Hlsl.Select() when possible https://github.com/Sergio0694/ComputeSharp/issues/735.
    //       This improves performance by 25%! (already tested in another branch).
    private static float4 Select(bool4 condition, float4 value1, float4 value2)
    {
        return Select(Hlsl.BoolToInt(condition), value1, value2);
    }

    private static float4 Select(int4 conditionInt, float4 value1, float4 value2)
    {
        return Hlsl.AsFloat(Select(conditionInt, Hlsl.AsInt(value1), Hlsl.AsInt(value2)));
    }

    private static int4 Select(bool4 condition, int4 value1, int4 value2)
    {
        return Select(Hlsl.BoolToInt(condition), value1, value2);
    }

    private static int4 Select(int4 conditionInt, int4 value1, int4 value2)
    {
        uint4 conditionMask = ~(uint4)(conditionInt - int4.One);
        return Hlsl.AsInt((conditionMask & Hlsl.AsUInt(value1)) | (~conditionMask & Hlsl.AsUInt(value2)));
    }
}
