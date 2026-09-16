namespace Y700Switch2V55Manager;

public sealed record GuideDetectionResult(
    bool SerialReady,
    bool NativeUsbDetected,
    string Summary,
    string NextAction);

public sealed record GuideFlashResult(
    bool Succeeded,
    OutputModeProfile Profile,
    string FailureCategory,
    string Summary,
    string NextAction);

public sealed record GuideUsbVerificationResult(
    bool Succeeded,
    OutputModeProfile Profile,
    string Summary,
    string NextAction);

public sealed record GuidePairingResult(
    bool Succeeded,
    string Summary,
    string NextAction);
