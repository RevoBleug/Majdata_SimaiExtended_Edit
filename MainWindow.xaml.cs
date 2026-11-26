using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DiscordRPC.Logging;
using MajdataEdit.AutoSaveModule;
using MajdataEdit.SvBrush;
using Microsoft.Win32;
using Un4seen.Bass;
using MajdataEdit.SyntaxModule;
using Timer = System.Timers.Timer;

namespace MajdataEdit;

/// <summary>
///     MainWindow.xaml 的交互逻辑
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs().Contains("--ForceSoftwareRender"))
        {
            MessageBox.Show("正在以软件渲染模式运行\nソフトウェア・レンダリング・モードで動作\nBooting as software rendering mode.");
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CheckAndStartView();

        TheWindow.Title = GetWindowsTitleString();

        SetWindowGoldenPosition();

        DCRPCclient.Logger = new ConsoleLogger { Level = LogLevel.Warning };
        DCRPCclient.Initialize();

        var handle = new WindowInteropHelper(this).Handle;
        Bass.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_CPSPEAKERS, handle);
        InitWave();

        ReadSoundEffect();
        ReadEditorSetting();

        chartChangeTimer.Elapsed += ChartChangeTimer_Elapsed;
        chartChangeTimer.AutoReset = false;
        currentTimeRefreshTimer.Elapsed += CurrentTimeRefreshTimer_Elapsed;
        currentTimeRefreshTimer.Start();
        visualEffectRefreshTimer.Elapsed += VisualEffectRefreshTimer_Elapsed;
        waveStopMonitorTimer.Elapsed += WaveStopMonitorTimer_Elapsed;
        playbackSpeedHideTimer.Elapsed += PlbHideTimer_Elapsed;

        if (editorSetting!.AutoCheckUpdate) CheckUpdate(true);

        #region 异常退出处理

        if (!SafeTerminationDetector.Of().IsLastTerminationSafe())
        {
            // 若上次异常退出，则询问打开恢复窗口
            var result = MessageBox.Show(GetLocalizedString("AbnormalTerminationInformation"),
                GetLocalizedString("Attention"), MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes)
            {
                var lastEditPath = File.ReadAllText(SafeTerminationDetector.Of().RecordPath).Trim();
                if (lastEditPath.Length != 0)
                    // 尝试打开上次未正常关闭的谱面 然后再打开恢复页面
                    try
                    {
                        initFromFile(lastEditPath);
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine(error.StackTrace);
                    }

                Menu_AutosaveRecover_Click(new object(), new RoutedEventArgs());
            }
        }

        SafeTerminationDetector.Of().RecordProgramClose();

        #endregion
    }


    //start the view and wait for boot, then set window pos
    private void SetWindowPosTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        var setWindowPosTimer = (Timer)sender!;
        Dispatcher.Invoke(() => { InternalSwitchWindow(); });
        setWindowPosTimer.Stop();
        setWindowPosTimer.Dispose();
    }

    //Window events
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!isSaved)
            if (!AskSave())
            {
                e.Cancel = true;
                return;
            }

        var process = Process.GetProcessesByName("MajdataView");
        if (process.Length > 0)
        {
            var result = MessageBox.Show(GetLocalizedString("AskCloseView"), GetLocalizedString("Attention"),
                MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes)
                process[0].Kill();
        }

        currentTimeRefreshTimer.Stop();
        visualEffectRefreshTimer.Stop();

        soundSetting.Close();
        //if (bpmtap != null) { bpmtap.Close(); }
        //if (muriCheck != null) { muriCheck.Close(); }
        SaveSetting();

        Bass.BASS_ChannelStop(bgmStream);
        Bass.BASS_StreamFree(bgmStream);
        Bass.BASS_ChannelStop(answerStream);
        Bass.BASS_StreamFree(answerStream);
        Bass.BASS_ChannelStop(breakStream);
        Bass.BASS_StreamFree(breakStream);
        Bass.BASS_ChannelStop(judgeExStream);
        Bass.BASS_StreamFree(judgeExStream);
        Bass.BASS_ChannelStop(hanabiStream);
        Bass.BASS_StreamFree(hanabiStream);
        Bass.BASS_Stop();
        Bass.BASS_Free();

        // 正常退出
        SafeTerminationDetector.Of().RecordProgramClose();
    }

    //Window grid events
    private void Grid_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            //Console.WriteLine(e.Data.GetData(DataFormats.FileDrop).ToString());
            if (e.Data.GetData(DataFormats.FileDrop).ToString() == "System.String[]")
            {
                var path = ((string[])e.Data.GetData(DataFormats.FileDrop))[0];
                if (path.ToLower().Contains("maidata.txt"))
                {
                    if (!isSaved)
                        if (!AskSave())
                            return;
                    var fileInfo = new FileInfo(path);
                    initFromFile(fileInfo.DirectoryName!);
                }
            }
    }

    private void FindClose_MouseDown(object sender, MouseButtonEventArgs e)
    {
        FindGrid.Visibility = Visibility.Collapsed;
        FumenContent.Focus();
    }

    #region MENU BARS

    private void Menu_New_Click(object sender, RoutedEventArgs e)
    {
        if (!isSaved)
            if (!AskSave())
                return;
        var openFileDialog = new OpenFileDialog
        {
            Filter = "track.mp3, track.ogg|track.mp3;track.ogg"
        };
        if ((bool)openFileDialog.ShowDialog()!)
        {
            var fileInfo = new FileInfo(openFileDialog.FileName);
            CreateNewFumen(fileInfo.DirectoryName!);
            initFromFile(fileInfo.DirectoryName!);
        }
    }

    private void Menu_Open_Click(object sender, RoutedEventArgs e)
    {
        if (!isSaved)
            if (!AskSave())
                return;
        var openFileDialog = new OpenFileDialog
        {
            Filter = "maidata.txt|maidata.txt"
        };
        if ((bool)openFileDialog.ShowDialog()!)
        {
            var fileInfo = new FileInfo(openFileDialog.FileName);
            initFromFile(fileInfo.DirectoryName!);
        }
    }

    private void Menu_Save_Click(object sender, RoutedEventArgs e)
    {
        SaveFumen(true);
        SystemSounds.Beep.Play();
    }

    private void Menu_SaveAs_Click(object sender, RoutedEventArgs e)
    {
    }

    private void Menu_ExportRender_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayAndPause(PlayMethod.Record);
    }

    private void MirrorLeftRight_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.Selection.Text, Mirror.HandleType.LRMirror);
        FumenContent.Selection.Text = result;
    }

    private void MirrorUpDown_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.Selection.Text, Mirror.HandleType.UDMirror);
        FumenContent.Selection.Text = result;
    }

    private void Mirror180_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.Selection.Text, Mirror.HandleType.HalfRotation);
        FumenContent.Selection.Text = result;
    }

    private void Mirror45_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.Selection.Text, Mirror.HandleType.Rotation45);
        FumenContent.Selection.Text = result;
    }

    private void MirrorCcw45_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.Selection.Text, Mirror.HandleType.CcwRotation45);
        FumenContent.Selection.Text = result;
    }

    private void BPMtap_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var tap = new BPMtap();
        tap.Owner = this;
        tap.Show();
    }

    private void MenuItem_InfomationEdit_Click(object? sender, RoutedEventArgs e)
    {
        var infoWindow = new Infomation();
        SetSavedState(false);
        infoWindow.ShowDialog();
        TheWindow.Title = GetWindowsTitleString(SimaiProcess.title!);
    }

    private void MenuItem_Majnet_Click(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo() { FileName = "https://majdata.net", UseShellExecute = true });
        //maidata.txtの譜面書式
    }

    private void MenuItem_GitHub_Click(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo() { FileName = "https://github.com/LingFeng-bbben/MajdataView", UseShellExecute = true });
    }

    private void MenuItem_SoundSetting_Click(object? sender, RoutedEventArgs e)
    {
        soundSetting = new SoundSetting
        {
            Owner = this
        };
        soundSetting.ShowDialog();
    }

    private void MuriCheck_Click_1(object? sender, RoutedEventArgs e)
    {
        var muriCheck = new MuriCheck
        {
            Owner = this
        };
        muriCheck.Show();
    }
    private void SyntaxCheckButton_Click(object sender, RoutedEventArgs e)
    {
        //ShowErrorWindow();
    }
    void ShowErrorWindow()
    {
        var mcrWindow = new MuriCheckResult
        {
            Owner = this
        };
        var errList = SyntaxChecker.ErrorList;
        errList.ForEach(e =>
        {
            e.positionY--;
            mcrWindow.errorPosition.Add(e);
            var eRow = new ListBoxItem
            {
                Content = e.eMessage,
                Name = "rr" + mcrWindow.CheckResult_Listbox.Items.Count
            };
            eRow.AddHandler(PreviewMouseDoubleClickEvent,
                new MouseButtonEventHandler(mcrWindow.ListBoxItem_PreviewMouseDoubleClick));
            mcrWindow.CheckResult_Listbox.Items.Add(eRow);
        });
        mcrWindow.Show();
    }
    private void SyntaxCheckButton_Click(object sender, MouseButtonEventArgs e)
    {
        //ShowErrorWindow();
    }
    private void MenuItem_EditorSetting_Click(object? sender, RoutedEventArgs e)
    {
        var esp = new EditorSettingPanel
        {
            Owner = this
        };
        esp.ShowDialog();
    }

    private void Menu_ResetViewWindow(object? sender, RoutedEventArgs e)
    {
        if (CheckAndStartView()) return;
        InternalSwitchWindow();
    }

    private void MenuFind_Click(object? sender, RoutedEventArgs e)
    {
        if (FindGrid.Visibility == Visibility.Collapsed)
        {
            FindGrid.Visibility = Visibility.Visible;
            InputText.Focus();
        }
        else
        {
            FindGrid.Visibility = Visibility.Collapsed;
        }
    }

    /*private void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        CheckUpdate();
    }*/

    private void Option1_Click(object? sender, RoutedEventArgs e)
    {
        SvBrushFromSelection();
    }

    private void Option2_Click(object? sender, RoutedEventArgs e)
    {
        return;
    }

    private void Option3_Click(object? sender, RoutedEventArgs e)
    {
        return;
    }

    //*SV 曲线刷

    /// <summary>选区两端的文本（去掉 \r，和 SimaiProcess 的下标口径一致）。</summary>
    private static string TextBetween(TextPointer a, TextPointer b)
    {
        return new TextRange(a, b).Text.Replace("\r", "");
    }

    /// <summary>把位置退到所在格子的开头（上一个逗号之后）。</summary>
    private static TextPointer MoveToCellStart(TextPointer p)
    {
        while (true)
        {
            var prev = p.GetNextInsertionPosition(LogicalDirection.Backward);
            if (prev == null) return p;
            if (TextBetween(prev, p).Contains(',')) return p;
            p = prev;
        }
    }

    /// <summary>把位置推到下一个逗号之后（含逗号），并吃掉尾部空白。</summary>
    private static TextPointer MoveToCellEnd(TextPointer p)
    {
        // 尾部空白先退掉，避免选区停在一格中间
        while (true)
        {
            var prev = p.GetNextInsertionPosition(LogicalDirection.Backward);
            if (prev == null) break;
            var t = TextBetween(prev, p);
            if (t.Length == 0) break;
            if (t.Trim().Length != 0) break;
            p = prev;
        }

        var before = p.GetNextInsertionPosition(LogicalDirection.Backward);
        if (before != null && TextBetween(before, p).Contains(',')) return p;

        while (true)
        {
            var next = p.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next == null) return p;
            var t = TextBetween(p, next);
            p = next;
            if (t.Contains(',')) return p;
        }
    }

    /// <summary>已打开的曲线刷窗口；同时只允许一个。</summary>
    private SvBrushWindow? svBrushWindow;

    private void SvBrushFromSelection()
    {
        // 独立窗口：没选中也能打开，选中内容后再载入
        if (svBrushWindow != null)
        {
            svBrushWindow.Activate();
            return;
        }

        var window = new SvBrushWindow(ReadSvSelectionForBrush, ApplySvBrushText)
        {
            Owner = this
        };
        window.Closed += (_, _) => svBrushWindow = null;
        svBrushWindow = window;
        window.Show();
    }

    /// <summary>把生成的文本写回编辑器。写入前重新吸附一次选区，防止选区已经被改过。</summary>
    private void ApplySvBrushText(string text)
    {
        var snap = ReadSvSelectionForBrush();
        var start = snap.Start;
        var end = snap.End;
        if (!snap.IsValid || start == null || end == null) return;

        FumenContent.Selection.Select(start, end);
        FumenContent.Selection.Text = text;
    }

    /// <summary>读取当前选区（吸附到整格），交给曲线刷窗口。</summary>
    private SvSelectionSnapshot ReadSvSelectionForBrush()
    {
        var snap = new SvSelectionSnapshot();
        var sel = FumenContent.Selection;

        if (sel.Start.CompareTo(sel.End) == 0)
        {
            snap.Message = "未选中内容。请回到编辑器里选中一段谱面（至少一整格），这里会自动载入。";
            return snap;
        }

        // 吸附到整格：起点退到上一个逗号后，终点推到下一个逗号后
        var start = MoveToCellStart(sel.Start);
        var end = MoveToCellEnd(sel.End);
        if (start.CompareTo(end) >= 0)
        {
            snap.Message = "选区里没有完整的格子，请重新选择。";
            return snap;
        }

        var selectionText = new TextRange(start, end).Text;
        if (string.IsNullOrEmpty(selectionText))
        {
            snap.Message = "选区里没有内容。";
            return snap;
        }

        // 含 GR 组的选区交给组内自己处理，这里先拒绝，避免破坏组结构
        if (selectionText.Contains("<GR", StringComparison.Ordinal) ||
            selectionText.Contains("</GR", StringComparison.Ordinal))
        {
            snap.Message = "选区里包含 GR 组，暂不支持。请把选区避开组，或先手动处理组内部分。";
            return snap;
        }

        snap.Start = start;
        snap.End = end;
        snap.Text = selectionText;
        snap.StartOffset = new TextRange(FumenContent.Document.ContentStart, start).Text.Replace("\r", "").Length;
        snap.EndOffset = new TextRange(FumenContent.Document.ContentStart, end).Text.Replace("\r", "").Length;

        // 选区时长 + 每格起始时刻：都用解析出来的逗号表算，不从音符推。
        // 每格时刻是 Still 的必需输入（选区内 BPM / 拍长可能变过，不能拿平均格长凑）。
        try
        {
            SimaiProcess.Serialize(GetRawFumenText(), snap.StartOffset);
            snap.Seconds = SimaiProcess.TimeAtPosition(snap.EndOffset) - SimaiProcess.TimeAtPosition(snap.StartOffset);

            var starts = SvCellText.SplitCellStarts(selectionText);
            var times = new double[starts.Count];
            for (var k = 0; k < starts.Count; k++)
            {
                var plain = snap.StartOffset + CountPlainChars(selectionText, starts[k]);
                times[k] = SimaiProcess.TimeAtPosition(plain);
            }

            snap.CellTimes = times;
        }
        catch
        {
            snap.Seconds = 0;
            snap.CellTimes = Array.Empty<double>();
        }

        return snap;
    }

    /// <summary>从字符串开头数到 upTo 位置有多少个字符（不计 \r，和 SimaiProcess 的下标口径一致）。</summary>
    private static int CountPlainChars(string s, int upTo)
    {
        var n = 0;
        for (var i = 0; i < upTo && i < s.Length; i++)
            if (s[i] != '\r')
                n++;
        return n;
    }


    private void Menu_AutosaveRecover_Click(object? sender, RoutedEventArgs e)
    {
        var asr = new AutoSaveRecover
        {
            Owner = this
        };
        asr.ShowDialog();
    }

    #endregion

    #region 快捷键

    private void PlayAndPause_CanExecute(object? sender, CanExecuteRoutedEventArgs e) //快捷键
    {
        TogglePlayAndStop();
    }

    private void StopPlaying_CanExecute(object? sender, CanExecuteRoutedEventArgs e) //快捷键
    {
        TogglePlayAndPause();
    }

    private void SaveFile_Command_CanExecute(object? sender, CanExecuteRoutedEventArgs e)
    {
        SaveFumen(true);
        SystemSounds.Beep.Play();
    }

    private void SendToView_CanExecute(object? sender, CanExecuteRoutedEventArgs e)
    {
        TogglePlayAndStop(PlayMethod.Op);
    }

    private void IncreasePlaybackSpeed_CanExecute(object? sender, CanExecuteRoutedEventArgs e)
    {
        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING) return;
        var speed = GetPlaybackSpeed();
        Console.WriteLine(speed);
        speed += 0.25f;
        PlbSpdLabel.Content = speed * 100 + "%";
        SetPlaybackSpeed(speed);
        PlbSpdAdjGrid.Visibility = Visibility.Visible;
        playbackSpeedHideTimer.Stop();
        playbackSpeedHideTimer.Start();
    }

    private void DecreasePlaybackSpeed_CanExecute(object? sender, CanExecuteRoutedEventArgs e)
    {
        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING) return;
        var speed = GetPlaybackSpeed();
        Console.WriteLine(speed);
        speed -= 0.25f;
        if (speed < 1e-6) return; // Interrupt if it's an epsilon or lower.
        PlbSpdLabel.Content = speed * 100 + "%";
        SetPlaybackSpeed(speed);
        PlbSpdAdjGrid.Visibility = Visibility.Visible;
        playbackSpeedHideTimer.Stop();
        playbackSpeedHideTimer.Start();
    }

    private readonly Timer playbackSpeedHideTimer = new(1000);

    private void PlbHideTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        Dispatcher.Invoke(() => { PlbSpdAdjGrid.Visibility = Visibility.Collapsed; });
        ((Timer)sender!).Stop();
    }

    private void FindCommand_CanExecute(object? sender, CanExecuteRoutedEventArgs e)
    {
        if (FindGrid.Visibility == Visibility.Collapsed)
        {
            FindGrid.Visibility = Visibility.Visible;
            InputText.Focus();
        }
        else
        {
            FindGrid.Visibility = Visibility.Collapsed;
        }
    }

    private void MirrorLRCommand_CanExecute(object? sender, CanExecuteRoutedEventArgs e)
    {
        MirrorLeftRight_MenuItem_Click(sender, null);
    }

    private void MirrorUDCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        MirrorUpDown_MenuItem_Click(sender, null);
    }

    private void Mirror180Command_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        Mirror180_MenuItem_Click(sender, null);
    }

    private void Mirror45Command_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        Mirror45_MenuItem_Click(sender, null);
    }

    private void MirrorCcw45Command_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        MirrorCcw45_MenuItem_Click(sender, null);
    }

    #endregion

    #region Left componients

    private void PlayAndPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayAndPause();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleStop();
    }

    private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = LevelSelector.SelectedIndex;
        SetRawFumenText(SimaiProcess.fumens[i]);
        selectedDifficulty = i;
        LevelTextBox.Text = SimaiProcess.levels[selectedDifficulty];
        SetSavedState(true);
        SimaiProcess.Serialize(GetRawFumenText());
        DrawWave();
        //SyntaxCheck();
    }

    private void LevelTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetSavedState(false);
        if (selectedDifficulty == -1) return;
        SimaiProcess.levels[selectedDifficulty] = LevelTextBox.Text;
    }

    private void OffsetTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetSavedState(false);
        try
        {
            SimaiProcess.first = float.Parse(OffsetTextBox.Text);
            SimaiProcess.Serialize(GetRawFumenText());
            DrawWave();
        }
        catch
        {
            SimaiProcess.first = 0f;
        }
    }

    private void OffsetTextBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var offset = float.Parse(OffsetTextBox.Text);
        offset += e.Delta > 0 ? 0.01f : -0.01f;
        OffsetTextBox.Text = offset.ToString();
    }

    private void FollowPlayCheck_Click(object sender, RoutedEventArgs e)
    {
        FumenContent.Focus();
    }

    private void Op_Button_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayAndStop(PlayMethod.Op);
    }

    private void SettingLabel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 单击设置的时候也可以进入设置界面
        var esp = new EditorSettingPanel();
        esp.Owner = this;
        esp.ShowDialog();
    }
    #endregion

    #region RichTextbox events

    private void FumenContent_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // 曲线刷窗口开着的话，让它跟着选区刷新（窗口内部自己防抖）
        svBrushWindow?.NotifyEditorSelectionChanged();

        NoteNowText.Content = "" + (
            new TextRange(FumenContent.Document.ContentStart, FumenContent.CaretPosition).Text.Replace("\r", "")
                .Count(o => o == '\n') + 1) + " 行";
        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING && (bool)FollowPlayCheck.IsChecked!)
            return;
        //按住Ctrl，同时按下鼠标左键/上下左右方向键时，才改变进度，其他包含Ctrl的组合键不影响进度。
        var needSeek = Keyboard.Modifiers == ModifierKeys.Control && (
            Mouse.LeftButton == MouseButtonState.Pressed ||
            Keyboard.IsKeyDown(Key.Left) ||
            Keyboard.IsKeyDown(Key.Right) ||
            Keyboard.IsKeyDown(Key.Up) ||
            Keyboard.IsKeyDown(Key.Down));

        // 这个事件在光标每一次移动（包括每敲一个字）都会触发。以前每次都整张谱面重解析，
        // 十几万字符的谱面要 25ms 左右，连按方向键就会明显卡。
        //   - 文本没变 → 走解析缓存 + 逗号表二分（微秒级）
        //   - 文本刚改过 → 等 chartChangeTimer 的延迟解析，这里先沿用上次的时间
        //   - 真的需要精确 seek（Ctrl + 拖动/方向键）→ 照旧完整解析
        var rawText = GetRawFumenText();
        double time;
        if (needSeek || SimaiProcess.HasFreshParse(rawText))
        {
            time = SimaiProcess.Serialize(rawText, GetRawFumenPosition());
        }
        else
        {
            // 文本刚改过、完整解析还没跟上。等 ChartRefreshDelay（默认 120ms）太难受，
            // 这里用只算逗号表的轻量路径当场算，输入时三角才能实时跟手。
            SimaiProcess.BuildCommaTableFast(rawText);
            time = SimaiProcess.TimeAtPosition(GetRawFumenPosition());
        }

        if (needSeek)
        {
            if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING)
                TogglePause();
            SetBgmPosition(time);
        }

        //Console.WriteLine("SelectionChanged");
        SimaiProcess.ClearNoteListPlayedState();
        ghostCusorPositionTime = (float)time;
        if (!isPlaying) DrawWave();
    }

    private void FumenContent_TextChanged(object sender, TextChangedEventArgs e)
    {
        var rawText = GetRawFumenText();
        if (rawText == "" || isLoading) return;
        SetSavedState(false);
        // 延迟解析是为了"十几万字符的谱面一次解析 20~30ms、连按会卡"。
        // 小谱面根本没这个负担，还按 120ms 延迟拖着，下边栏的音符位置会慢一拍，手感很差。
        // 所以只有超出快解阈值、并且用户确实把延迟调大了，才走定时器。
        if (chartChangeTimer.Interval < 33 || rawText.Length <= FastParseCharLimit)
        {
            chartChangeTimer.Stop();
            ghostCusorPositionTime = (float)SimaiProcess.Serialize(rawText, GetRawFumenPosition());
            DrawWave();
        }
        else
        {
            chartChangeTimer.Stop();
            chartChangeTimer.Start();
        }
    }

    private void FumenContent_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 按下Insert键，同时未按下任何组合键，切换覆盖模式
        if (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.None)
        {
            SwitchFumenOverwriteMode();
            e.Handled = true;
        }
    }

    #endregion

    #region Wave displayer

    private void WaveViewZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (deltatime > 1)
            deltatime -= 1;
        DrawWave();
        FumenContent.Focus();
    }

    private void WaveViewZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (deltatime < 10)
            deltatime += 1;
        DrawWave();
        FumenContent.Focus();
    }

    private void MusicWave_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollWave(-e.Delta);
    }

    private void MusicWave_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        lastMousePointX = e.GetPosition(this).X;
    }

    private void MusicWave_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = e.GetPosition(this).X - lastMousePointX;
            lastMousePointX = e.GetPosition(this).X;
            ScrollWave(-delta);
        }

        lastMousePointX = e.GetPosition(this).X;
    }

    private void MusicWave_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        InitWave();
        DrawWave();
    }


    #endregion

    
}