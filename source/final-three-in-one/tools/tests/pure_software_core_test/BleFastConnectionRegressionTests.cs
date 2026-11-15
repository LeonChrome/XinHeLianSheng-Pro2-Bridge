using System;
using Y700Switch2V60Viiper;

internal static class BleFastConnectionRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        BleAdaptiveMeasurement Sample(int count = 200, int? parsed = null, double duration = 1.5,
            double age = 3, double p95 = 8, double gap = 16) =>
            new(new(duration, count, parsed ?? count, age, true, false, p95, null), 0, gap);
        bool High(BleAdaptiveMeasurement value) => BleFastConnectionPolicy.IsHigh(Sample(), value);
        bool Compatible(BleAdaptiveMeasurement value) => BleFastConnectionPolicy.IsCompatible(Sample(100, p95: 16), value);

        check(High(Sample()), "two independent actual 133Hz short windows allow fast startup without an API request");
        check(High(Sample(192)), "128Hz actual FD2 and parsed boundary is accepted without inventing a ten second capture");
        check(!High(Sample(191)), "a short-window rate below 128Hz cannot become high-rate admission");
        check(!High(Sample(100, p95: 16)), "one high burst followed by 66Hz cannot pass high-rate admission");
        check(Compatible(Sample(100, p95: 16)), "native Win10 or Win11 actual 66Hz can enter compatibility mode");
        check(Compatible(Sample(90, p95: 30, gap: 60)), "exact 60Hz compatibility boundary is admitted");
        check(!Compatible(Sample(89, p95: 20)), "compatibility never promises 60Hz from fewer real samples");
        check(!BleFastConnectionPolicy.IsHigh(Sample(100, p95: 16), Sample()), "both windows must meet the high-rate requirement");
        check(!BleFastConnectionPolicy.IsCompatible(Sample(30, p95: 50), Sample(100, p95: 16)), "a low first window cannot be hidden by later recovery");

        var repeated = Sample();
        check(!BleFastConnectionPolicy.IsHigh(repeated, repeated), "one measurement cannot be reused as two independent windows");
        check(!BleFastConnectionPolicy.IsHigh(repeated, repeated with { CompletedAgeSeconds = .1 }), "the same underlying count capture cannot be reused with another age");
        check(High(Sample(200, 198)), "99 percent parsed ratio boundary is accepted when parsed Hz also meets target");
        check(!High(Sample(200, 197)), "parse losses above one percent do not pass a fast startup gate");
        check(!High(Sample(200, 201)), "more parsed than received counts are invalid");
        check(!High(Sample(-1)), "negative counters cannot manufacture input");
        check(!High(Sample(0)), "zero input never passes");

        foreach (double duration in new[] { 0, -1, 1.499, 4.001, double.NaN, double.PositiveInfinity })
            check(!High(Sample(duration: duration)), "short incomplete or unbounded duration fails fast admission " + duration);
        check(High(Sample(512, duration: 4)), "actual four second 128Hz capture is within the bounded fast policy");
        foreach (double age in new[] { -1, 40, double.NaN, double.PositiveInfinity })
            check(!High(Sample(age: age)), "stale or invalid tail fails high-rate admission " + age);
        check(!Compatible(Sample(100, age: 60, p95: 16)), "compatibility also requires a fresh input tail");
        check(!High(Sample() with { Sample = Sample().Sample with { Connected = false } }), "disconnected window is not admitted");
        foreach (double age in new[] { -1, 4.001, double.NaN, double.PositiveInfinity })
            check(!High(Sample() with { CompletedAgeSeconds = age }), "old or invalid window age cannot pass " + age);
        foreach (double gap in new[] { -1, 40.001, double.NaN, double.PositiveInfinity })
            check(!High(Sample(gap: gap)), "a burst rate cannot conceal excessive or invalid maximum silence " + gap);
        check(!High(Sample(p95: 12.01)), "high admission bounds percentile jitter");
        check(!Compatible(Sample(100, p95: 30.01)), "compatibility admission bounds percentile jitter");
        check(!Compatible(Sample(100, p95: 16, gap: 60.01)), "compatibility admission bounds maximum silence");
        check(!High(Sample() with { MaximumGapMs = null }), "unknown maximum silence cannot prove a short window is healthy");
        check(!High(Sample() with { Sample = Sample().Sample with { P95GapMs = null } }), "unknown percentile cannot prove fast-window quality");
        check(!High(Sample(p95: 8, gap: 7)), "inconsistent percentile above maximum gap is invalid");

        BleFastConnectionWindow Window(double start, double end, long sequence = 7) => new(Sample(), start, end, sequence);
        check(BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(11.5, 13)), "monotonic adjacent captures explicitly prove non-overlap");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(10, 11.5)), "duplicate capture bounds are rejected even with distinct records");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(11.4, 12.9)), "partly overlapping captures cannot count packets twice");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(12, 13.5)), "separated captures are not continuous fast admission evidence");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(11.5, 13, 8)), "a new physical connection invalidates the earlier gate window");
        check(!BleFastConnectionPolicy.IsHigh(Window(10, 11.5), Window(11.5, 15)), "capture bounds must agree with the measured duration");
        check(!BleFastConnectionPolicy.IsHigh(Window(double.NaN, 11.5), Window(11.5, 13)), "invalid monotonic capture bounds cannot pass");
    }
}
