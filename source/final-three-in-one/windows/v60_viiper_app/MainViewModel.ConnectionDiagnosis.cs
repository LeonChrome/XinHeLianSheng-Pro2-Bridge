namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private Pro2ControllerSlot? ConnectionWarningSlot => pro2Slots.FirstOrDefault(s =>
        s.Enabled && s.PhysicalInput == null && !s.IsSleeping && !s.InputSource.IsRunning && s.InputSource.ConnectionDiagnosis.Visible);
    public bool HasConnectionWarning => ConnectionWarningSlot != null;
    public string ConnectionWarningTitle => ConnectionWarningSlot is { } s
        ? s.Name + " · " + s.InputSource.ConnectionDiagnosis.Title : "";
    public string ConnectionWarningAdvice => ConnectionWarningSlot?.InputSource.ConnectionDiagnosis.Advice ?? "";
    public string ConnectionWarningEvidence => ConnectionWarningSlot is { } s
        ? EnvironmentHomeSummary + "\n" + s.InputSource.ConnectionDiagnosticText : "";
    private void RefreshConnectionDiagnosis()
    {
        foreach (string name in new[] { nameof(HasConnectionWarning), nameof(ConnectionWarningTitle),
            nameof(ConnectionWarningAdvice), nameof(ConnectionWarningEvidence) }) OnPropertyChanged(name);
    }
}
