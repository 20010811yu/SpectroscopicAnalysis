namespace SpectroscopicAnalysis.Host.Ui;

/// <summary>
/// 确认弹窗抽象（开发规范 3.3：VM 禁止引用 WPF 类型，弹窗经此接口解耦；
/// 实现层使用 HandyControl MessageBox，测试可替换为 Fake）。
/// </summary>
public interface IDialogService
{
    /// <summary>弹出确认对话框；返回 true 表示用户确认。</summary>
    bool Confirm(string title, string message);
}
