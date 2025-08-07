#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
#pragma warning disable CS8622
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace RYCBEditorX.ConPty;
/// <summary>
/// TerminalControl.xaml 的交互逻辑
/// </summary>
public partial class TerminalControl : UserControl, IDisposable
{
    private ConPtySession _session;
    private readonly SolidColorBrush _defaultForeground = Brushes.White;
    private readonly SolidColorBrush _defaultBackground = Brushes.Black;

    public TerminalControl()
    {
        InitializeComponent();
        OutputBox.FontFamily = new FontFamily("Consolas");
        InputBox.Focus();
    }

    public void StartTerminal(string shell = "powershell.exe")
    {
        _session?.Dispose();
        _session = new ConPtySession(shell, 120, 30);
        OutputBox.Document.Blocks.Clear();
        _session.OutputReceived += OnOutputReceived;
    }

    private void OnOutputReceived(object sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            AppendText(text, _defaultForeground, _defaultBackground);
            OutputBox.ScrollToEnd();
        });
    }


    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            //    _session.InputWriter.WriteLine(InputBox.Text);
            //    _session.InputWriter.Flush();
            //    AppendText(InputBox.Text + "\n", Brushes.Cyan, _defaultBackground);
            //    InputBox.Clear();
            //    e.Handled = true;
            if (InputBox.Text.Equals("exit"))
            {
                InputBox.Clear();
                e.Handled = true;
                return;
            }
            if (IsClearCommand(InputBox.Text))
            {
                ClearOutput(); // 手动清空界面
                return; // 不发送到子进程
            }
            await _session.SendCommandAsync(InputBox.Text);

            // 读取输出（建议使用后台线程或Task）
            var output = await Task.Run(() => _session.ReadOutputAsync());
            OutputBox.AppendText(output);
            InputBox.Clear();
            e.Handled = true;
        }
    }

    private static bool IsClearCommand(string command)
    {
        return command.Trim().Equals("cls", StringComparison.OrdinalIgnoreCase) ||
               command.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase);
    }

    private void ClearOutput()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 清空内容
            OutputBox.Document.Blocks.Clear();

            // 添加新提示符（模拟真实控制台）
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run("PS " + GetCurrentPath() + "> "));
            OutputBox.Document.Blocks.Add(paragraph);

            // 滚动到底部
            OutputBox.ScrollToEnd();
        });
    }

    private string GetCurrentPath()
    {
        // 实现获取当前路径的逻辑（如需）
        return Directory.GetCurrentDirectory();
    }

    private void AppendText(string text, Brush foreground, Brush background)
    {
        if (text.ContainsAny(["错误", "Error", "Err", "Exception", "无法将"]))
        {
            foreground = Brushes.Red;
        }
        var run = new Run(text)
        {
            Foreground = foreground,
            Background = background
        };

        if (OutputBox.Document.Blocks.LastBlock is Paragraph para)
        {
            para.Inlines.Add(run);
        }
        else
        {
            var newPara = new Paragraph(run);
            OutputBox.Document.Blocks.Add(newPara);
        }
    }

    public void Dispose() => _session?.Dispose();

    // 公开样式设置方法
    public void SetForeground(Brush brush) => OutputBox.Foreground = brush;
    public void SetBackground(Brush brush) => OutputBox.Background = brush;
    public void SetFont(FontFamily font, double size)
    {
        OutputBox.FontFamily = font;
        OutputBox.FontSize = size;
    }
}

public static class Ex
{
    public static bool ContainsAny(this string str, params string[] substrings)
    {
        foreach (var substring in substrings)
        {
            if (str.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}