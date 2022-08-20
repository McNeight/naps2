using NAPS2.Update;

namespace NAPS2.WinForms;

public interface INotificationManager : ISaveNotify
{
    void DonatePrompt();
    void OperationProgress(OperationProgress opModalProgress, IOperation op);
    void UpdateAvailable(IUpdateChecker updateChecker, UpdateInfo update);
    void Rebuild();
}