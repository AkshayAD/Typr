using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Typr.Core;
using Typr.Infrastructure;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// WPF permits one Application per process. All cases share one dispatcher and never
// run App.OnStartup, display a window, load a model, install hooks or touch the clipboard.
[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfCollection : ICollectionFixture<WpfFixture> { }

public sealed class WpfFixture : IDisposable
{
    readonly Thread thread;
    readonly Dispatcher dispatcher;

    public WpfFixture()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() => {
            try {
                var application = new Application();
                // Load the actual product styles without constructing App, whose
                // constructor queues its startup callback even when Run is never called.
                var source = XElement.Load(Path.Combine(AppContext.BaseDirectory, "ProductApp.xaml"));
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = new XElement(presentation + "ResourceDictionary",
                    source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
                    source.Element(presentation + "Application.Resources")!.Elements());
                application.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true, Name = "Typr WPF integration tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        dispatcher = ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    public void Run(Action action) => dispatcher.Invoke(action);
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    public void Dispose()
    {
        dispatcher.Invoke(() => {
            Application.Current?.Shutdown();
            // This fixture owns Dispatcher.Run rather than Application.Run, so it
            // also owns terminating that loop after application cleanup is queued.
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("The WPF test dispatcher did not shut down.");
    }

}

[Collection("WPF")]
public sealed class WindowIntegrationTests(WpfFixture fixture)
{
    static readonly string[] Sections = ["Workspace", "History", "Vocabulary", "Snippets", "Appearance", "Settings"];

    [Fact] public void FreshProfileNavigatesEveryPageAndRoutesSetupWithoutOpeningMicrophone() => fixture.Run(() => {
        using var profile = new TestProfile();
        using var session = profile.Open();
        var window = session.Window;
        Assert.Contains("Typr", window.Title);
        Assert.False(Control<Button>(window, "PracticeButton").IsEnabled);
        Assert.False(Control<Button>(window, "PauseButton").IsEnabled);
        Assert.False(Control<CheckBox>(window, "HistoryEnabledBox").IsChecked);
        Assert.False(Control<CheckBox>(window, "AudioRetentionBox").IsChecked);
        Assert.False(File.Exists(profile.SettingsPath));
        foreach (var section in Sections) {
            Click(Control<RadioButton>(window, section + "Nav"));
            foreach (var other in Sections)
                Assert.Equal(other == section ? Visibility.Visible : Visibility.Collapsed, Control<FrameworkElement>(window, other + "Page").Visibility);
            Assert.True(Control<RadioButton>(window, section + "Nav").IsChecked);
        }
        Click(Control<RadioButton>(window, "WorkspaceNav"));
        Click(Control<Button>(window, "HeroButton"));
        Assert.Equal(Visibility.Visible, Control<FrameworkElement>(window, "SettingsPage").Visibility);
        Assert.False(Control<Button>(window, "PracticeButton").IsEnabled);
        Assert.False(Directory.Exists(Path.Combine(profile.LocalDirectory, "audio")));
    });

    [Fact] public void AppearanceChangesActualResourcesAndPersistsAcrossReopen() => fixture.Run(() => {
        using var profile = new TestProfile();
        using (var session = profile.Open()) {
            var window = session.Window;
            Control<ComboBox>(window, "ThemeBox").SelectedIndex = 2;
            Control<ComboBox>(window, "AccentBox").SelectedIndex = 1;
            WpfFixture.Pump();
            var saved = profile.ReadSettings();
            Assert.Equal("Dark", saved.Theme);
            Assert.Equal("Forest", saved.Accent);
            var expectedCanvas = SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromRgb(27, 29, 28);
            var expectedAccent = SystemParameters.HighContrast ? SystemColors.WindowTextColor : Color.FromRgb(145, 207, 172);
            Assert.Equal(expectedCanvas, ((SolidColorBrush)window.Background).Color);
            Assert.Equal(expectedAccent, ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color);
        }
        using var reopened = profile.Open();
        Assert.Equal(2, Control<ComboBox>(reopened.Window, "ThemeBox").SelectedIndex);
        Assert.Equal(1, Control<ComboBox>(reopened.Window, "AccentBox").SelectedIndex);
    });

    [Fact] public void VocabularyButtonsValidateCreatePersistRejectDuplicateAndRemove() => fixture.Run(() => {
        using var profile = new TestProfile();
        using var session = profile.Open();
        var window = session.Window;
        Click(Control<RadioButton>(window, "VocabularyNav"));
        var add = ButtonWithContent(window, "Add to vocabulary");
        Click(add);
        Assert.Empty(Control<ListBox>(window, "VocabularyList").Items);
        Assert.Contains("both", Control<TextBlock>(window, "VocabularyFeedback").Text);
        Control<TextBox>(window, "VocabularyPhrase").Text = "  type er  ";
        Control<TextBox>(window, "VocabularyReplacement").Text = "Typr";
        Click(add);
        Assert.Equal(new TextReplacement("type er", "Typr"), Assert.Single(profile.ReadSettings().Vocabulary));
        Assert.Single(Control<ListBox>(window, "VocabularyList").Items);
        Assert.Empty(Control<TextBox>(window, "VocabularyPhrase").Text);
        Assert.Equal(Visibility.Collapsed, Control<TextBlock>(window, "VocabularyEmpty").Visibility);
        Control<TextBox>(window, "VocabularyPhrase").Text = "TYPE ER";
        Control<TextBox>(window, "VocabularyReplacement").Text = "Different";
        Click(add);
        Assert.Single(profile.ReadSettings().Vocabulary);
        Assert.Contains("already", Control<TextBlock>(window, "VocabularyFeedback").Text);
        Control<ListBox>(window, "VocabularyList").SelectedIndex = 0;
        Click(ButtonWithContent(window, "Remove selected word"));
        Assert.Empty(profile.ReadSettings().Vocabulary);
        Assert.Empty(Control<ListBox>(window, "VocabularyList").Items);
        Assert.Equal(Visibility.Visible, Control<TextBlock>(window, "VocabularyEmpty").Visibility);
    });

    [Fact] public void SnippetButtonsPreserveLiteralMultilineTextAndRejectCrossListDuplicate() => fixture.Run(() => {
        using var profile = new TestProfile();
        profile.WriteSettings(new AppSettings { Vocabulary = [new("type er", "Typr")] });
        const string expansion = "Thanks,\r\nExample $1 \\ team";
        using (var session = profile.Open()) {
            var window = session.Window;
            Click(Control<RadioButton>(window, "SnippetsNav"));
            Control<TextBox>(window, "SnippetPhrase").Text = "TYPE ER";
            Control<TextBox>(window, "SnippetReplacement").Text = expansion;
            Click(ButtonWithContent(window, "Create snippet"));
            Assert.Empty(profile.ReadSettings().Snippets);
            Assert.Contains("already", Control<TextBlock>(window, "SnippetFeedback").Text);
            Control<TextBox>(window, "SnippetPhrase").Text = "my signature";
            Click(ButtonWithContent(window, "Create snippet"));
            Assert.Equal(expansion, Assert.Single(profile.ReadSettings().Snippets).Replacement);
        }
        using var reopened = profile.Open();
        var list = Control<ListBox>(reopened.Window, "SnippetsList");
        Assert.Equal(expansion, Assert.IsType<TextReplacement>(Assert.Single(list.Items.Cast<object>())).Replacement);
        list.SelectedIndex = 0;
        Click(ButtonWithContent(reopened.Window, "Remove selected snippet"));
        Assert.Empty(profile.ReadSettings().Snippets);
        Assert.Equal(Visibility.Visible, Control<TextBlock>(reopened.Window, "SnippetsEmpty").Visibility);
    });

    [Fact] public void HistorySearchSelectionAndRemovalUseThePersistedStore() => fixture.Run(() => {
        using var profile = new TestProfile();
        var history = new TranscriptHistoryStore(profile.HistoryPath);
        history.Add("First synthetic result", "Clean");
        history.Add("Second UNIQUE result", "Raw");
        using var session = profile.Open();
        var window = session.Window;
        Click(Control<RadioButton>(window, "HistoryNav"));
        var list = Control<ListBox>(window, "HistoryList");
        Assert.Equal(2, list.Items.Count);
        Control<TextBox>(window, "HistorySearchBox").Text = "unique";
        Assert.Single(list.Items);
        list.SelectedIndex = 0;
        Assert.Equal("Second UNIQUE result", Control<TextBox>(window, "HistoryDetail").Text);
        Assert.Equal(Visibility.Visible, Control<TextBox>(window, "HistoryDetail").Visibility);
        Assert.True(Control<Button>(window, "CopyHistoryButton").IsEnabled);
        Click(Control<Button>(window, "RemoveHistoryButton"));
        Assert.Empty(list.Items);
        Assert.Equal(Visibility.Visible, Control<TextBlock>(window, "HistoryEmpty").Visibility);
        Assert.Contains("No matches", Control<TextBlock>(window, "HistoryEmpty").Text);
        Assert.Equal(Visibility.Collapsed, Control<TextBox>(window, "HistoryDetail").Visibility);
        Assert.False(Control<Button>(window, "CopyHistoryButton").IsEnabled);
        history.Load();
        Assert.Equal("First synthetic result", Assert.Single(history.Entries).Text);
        Control<TextBox>(window, "HistorySearchBox").Clear();
        Assert.Single(list.Items);
    });

    [Fact] public void CorruptSettingsArePreservedWhileSessionAppearanceStillWorks() => fixture.Run(() => {
        using var profile = new TestProfile();
        const string original = "{invalid settings";
        File.WriteAllText(profile.SettingsPath, original);
        using var session = profile.Open();
        var window = session.Window;
        Assert.Contains("preserved", Control<TextBlock>(window, "StatusText").Text);
        Control<ComboBox>(window, "ThemeBox").SelectedIndex = 2;
        Control<ComboBox>(window, "AccentBox").SelectedIndex = 2;
        WpfFixture.Pump();
        Assert.Equal(original, File.ReadAllText(profile.SettingsPath));
        Assert.Contains("session", Control<TextBlock>(window, "StatusText").Text);
        Assert.False(File.Exists(profile.SettingsPath + ".tmp"));
        var expectedCanvas = SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromRgb(27, 29, 28);
        Assert.Equal(expectedCanvas, ((SolidColorBrush)window.Background).Color);
    });

    [Fact] public void PreferenceEventsPersistValuesAndConfigureTheRealCaptionOverlay() => fixture.Run(() => {
        using var profile = new TestProfile();
        profile.WriteSettings(new AppSettings { SavedAudioLimit = 7, CaptionOpacity = .42 });
        using (var session = profile.Open()) {
            var window = session.Window;
            Toggle(window, "HistoryEnabledBox", true);
            Control<ComboBox>(window, "OutputModeBox").SelectedIndex = 1;
            Control<ComboBox>(window, "CaptionLinesBox").SelectedIndex = 2;
            Assert.Equal(7, profile.ReadSettings().SavedAudioLimit);
            Assert.Equal(.42, profile.ReadSettings().CaptionOpacity);
            Control<ComboBox>(window, "CaptionOpacityBox").SelectedIndex = 2;
            Toggle(window, "BarVisibleBox", false);
            var overlay = Assert.Single(Application.Current.Windows.OfType<CaptionOverlay>());
            overlay.SetLines(["one", "two", "three", "four"]);
            var root = Assert.IsType<StackPanel>(overlay.Content);
            var lines = Assert.IsType<StackPanel>(root.Children[0]);
            Assert.Equal(3, lines.Children.Count);
            Assert.Equal("two", Assert.IsType<TextBlock>(Assert.IsType<Border>(lines.Children[0]).Child).Text);
            Assert.Equal(byte.MaxValue, Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(lines.Children[0]).Background).Color.A);
            Assert.Equal(Visibility.Collapsed, root.Children[1].Visibility);
            Toggle(window, "CaptionsBox", false);
            overlay.SetLines(["Must stay hidden"]);
            Assert.Empty(lines.Children);
            Toggle(window, "AudioRetentionBox", false);
            Assert.Equal(0, profile.ReadSettings().SavedAudioLimit);
            Toggle(window, "AudioRetentionBox", true);
            Assert.Equal(200, profile.ReadSettings().SavedAudioLimit);
        }
        using var reopened = profile.Open();
        var saved = profile.ReadSettings();
        Assert.True(saved.SaveHistory);
        Assert.False(saved.PolishEnabled);
        Assert.False(saved.LiveCaptionsEnabled);
        Assert.False(saved.BarVisible);
        Assert.Equal(3, saved.CaptionMaxLines);
        Assert.Equal(1, saved.CaptionOpacity);
        Assert.True(Control<CheckBox>(reopened.Window, "HistoryEnabledBox").IsChecked);
        Assert.Equal(1, Control<ComboBox>(reopened.Window, "OutputModeBox").SelectedIndex);
        Assert.False(Control<CheckBox>(reopened.Window, "CaptionsBox").IsChecked);
        Assert.Equal(2, Control<ComboBox>(reopened.Window, "CaptionLinesBox").SelectedIndex);
    });

    [Fact] public void CorruptHistoryIsPreservedAndClearlyExplainedWhenPreferencesChange() => fixture.Run(() => {
        using var profile = new TestProfile();
        const string original = "{invalid history";
        File.WriteAllText(profile.HistoryPath, original);
        using var session = profile.Open();
        var window = session.Window;
        Assert.Contains("History couldn't be read", Control<TextBlock>(window, "StatusText").Text);
        Assert.Contains("preserved", Control<TextBlock>(window, "HistoryEmpty").Text);
        Assert.Empty(Control<ListBox>(window, "HistoryList").Items);
        Toggle(window, "HistoryEnabledBox", true);
        Click(Control<Button>(window, "RemoveHistoryButton"));
        Assert.Equal(original, File.ReadAllText(profile.HistoryPath));
        Assert.Empty(Control<ListBox>(window, "HistoryList").Items);
    });

    [Fact] public void FailedSettingsWriteHonorsSessionPreferencesAndPreservesTheBlockingDirectory() => fixture.Run(() => {
        using var profile = new TestProfile();
        Directory.CreateDirectory(profile.SettingsPath);
        var marker = Path.Combine(profile.SettingsPath, "preserve.txt");
        File.WriteAllText(marker, "existing data");
        using var session = profile.Open();
        var window = session.Window;
        Control<ComboBox>(window, "ThemeBox").SelectedIndex = 2;
        Control<ComboBox>(window, "AccentBox").SelectedIndex = 2;
        Toggle(window, "HistoryEnabledBox", true);
        Toggle(window, "CaptionsBox", false);
        Toggle(window, "BarVisibleBox", false);
        Assert.Contains("session", Control<TextBlock>(window, "StatusText").Text);
        Assert.Contains("couldn't save", Control<TextBlock>(window, "StatusText").Text);
        Assert.True(Directory.Exists(profile.SettingsPath));
        Assert.Equal("existing data", File.ReadAllText(marker));
        var expectedCanvas = SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromRgb(27, 29, 28);
        Assert.Equal(expectedCanvas, ((SolidColorBrush)window.Background).Color);
        var overlay = Assert.Single(Application.Current.Windows.OfType<CaptionOverlay>());
        overlay.SetLines(["Must stay hidden"]);
        var root = Assert.IsType<StackPanel>(overlay.Content);
        Assert.Empty(Assert.IsType<StackPanel>(root.Children[0]).Children);
        Assert.Equal(Visibility.Collapsed, root.Children[1].Visibility);
    });

    [Fact] public void CompanionPreferencesPersistPasteProviderModelsReasoningAndIndependentShortcuts() => fixture.Run(() => {
        using var profile = new TestProfile();
        using (var session = profile.Open()) {
            var window = session.Window;
            Toggle(window, "AutoPasteBox", false);
            Toggle(window, "AiPolishBox", false);
            Toggle(window, "CommandShortcutsBox", false);
            Control<ComboBox>(window, "MicrophoneShortcutBox").SelectedItem = "F9";
            Control<ComboBox>(window, "HandsFreeShortcutBox").SelectedItem = "Ctrl+Shift+R";
            Control<ComboBox>(window, "PasteShortcutBox").SelectedItem = "F10";
            Control<ComboBox>(window, "ProviderPriorityBox").SelectedIndex = 3;
            Control<ComboBox>(window, "ProviderModelBox").Text = "fixture-claude";
            Control<ComboBox>(window, "ReasoningBox").SelectedItem = "high";
            var saved = profile.ReadSettings();
            Assert.False(saved.AutoPasteEnabled); Assert.False(saved.AiPolishEnabled); Assert.False(saved.CommandShortcutsEnabled);
            Assert.Equal("Claude first", saved.ProviderPriority);
            Assert.Equal("fixture-claude", saved.ClaudeModel);
            Assert.Equal("high", saved.PolishReasoning);
            Assert.Equal("F9", saved.MicrophoneShortcut);
            Assert.Equal("Ctrl+Shift+R", saved.HandsFreeShortcut);
            Assert.Equal("F10", saved.PasteShortcut);
            Control<ComboBox>(window, "ProviderPriorityBox").SelectedIndex = 0;
            Control<ComboBox>(window, "ProviderModelBox").Text = "fixture-chatgpt";
            Control<ComboBox>(window, "ReasoningBox").SelectedItem = "medium";
            Control<ComboBox>(window, "RetranscriptionModelBox").SelectedValue = "whisper-large-v3";
            saved = profile.ReadSettings();
            Assert.Equal("fixture-chatgpt", saved.ChatGptModel);
            Assert.Equal("fixture-claude", saved.ClaudeModel);
            Assert.Equal("whisper-large-v3", saved.RetranscriptionModel);
            Assert.Contains("Download", Control<TextBlock>(window, "HistoryOperationStatus").Text);
            Assert.All(Application.Current.Windows.OfType<DictationBarWindow>(), bar => Assert.False(bar.IsVisible));
        }
        using var reopened = profile.Open();
        Assert.False(Control<CheckBox>(reopened.Window, "AutoPasteBox").IsChecked);
        Assert.False(Control<CheckBox>(reopened.Window, "AiPolishBox").IsChecked);
        Assert.Equal("fixture-chatgpt", Control<ComboBox>(reopened.Window, "ProviderModelBox").Text);
        Assert.Equal("medium", Control<ComboBox>(reopened.Window, "ReasoningBox").SelectedItem);
        Assert.Equal("F9", Control<ComboBox>(reopened.Window, "MicrophoneShortcutBox").SelectedItem);
        Assert.Equal("Ctrl+Shift+R", Control<ComboBox>(reopened.Window, "HandsFreeShortcutBox").SelectedItem);
        Assert.Equal("F10", Control<ComboBox>(reopened.Window, "PasteShortcutBox").SelectedItem);
        Assert.Equal("whisper-large-v3", Control<ComboBox>(reopened.Window, "RetranscriptionModelBox").SelectedValue);
    });

    [Fact] public void HoverAndRightClickActionsRemainIndependentThroughSaveAndReopen() => fixture.Run(() => {
        using var profile = new TestProfile();
        using (var session = profile.Open()) {
            var window = session.Window;
            var holdHover = CheckBoxNamed(window, "Hold to talk on hover");
            var holdContext = CheckBoxNamed(window, "Hold to talk on right-click");
            Assert.True(holdHover.IsChecked); Assert.False(holdContext.IsChecked);
            holdHover.IsChecked = false; Click(holdHover);
            holdContext.IsChecked = true; Click(holdContext);
            var hideHover = CheckBoxNamed(window, "Hide bar on hover");
            var hideContext = CheckBoxNamed(window, "Hide bar on right-click");
            hideHover.IsChecked = true; Click(hideHover);
            hideContext.IsChecked = false; Click(hideContext);
            var saved = profile.ReadSettings();
            Assert.DoesNotContain("HoldToTalk", saved.BarHoverActions); Assert.Contains("HoldToTalk", saved.BarContextActions);
            Assert.Contains("HideBar", saved.BarHoverActions); Assert.DoesNotContain("HideBar", saved.BarContextActions);
        }
        using var reopened = profile.Open();
        Assert.False(CheckBoxNamed(reopened.Window, "Hold to talk on hover").IsChecked);
        Assert.True(CheckBoxNamed(reopened.Window, "Hold to talk on right-click").IsChecked);
        Assert.True(CheckBoxNamed(reopened.Window, "Hide bar on hover").IsChecked);
        Assert.False(CheckBoxNamed(reopened.Window, "Hide bar on right-click").IsChecked);
    });

    [Fact] public void ChangingProviderImmediatelyRemovesModelsFromThePreviousProvider() => fixture.Run(() => {
        using var profile = new TestProfile();
        using var session = profile.Open();
        var window = session.Window;
        var modelBox = Control<ComboBox>(window, "ProviderModelBox");
        var previous = new PolishModel("fixture-chatgpt", "Synthetic ChatGPT model", new[] { "low", "high" }, "low");
        modelBox.ItemsSource = new[] { previous }; modelBox.SelectedItem = previous;
        Assert.Equal("fixture-chatgpt", profile.ReadSettings().ChatGptModel);
        Control<ComboBox>(window, "ProviderPriorityBox").SelectedIndex = 1;
        Assert.DoesNotContain(modelBox.Items.Cast<object>(), item => ReferenceEquals(item, previous));
        Assert.Equal("claude-haiku-5-5", modelBox.Text);
        Assert.Equal("claude-haiku-5-5", profile.ReadSettings().ClaudeModel);
    });

    [Fact] public void SavedNoneReasoningIsVisibleWithoutStartingAProviderConnection() => fixture.Run(() => {
        using var profile = new TestProfile();
        profile.WriteSettings(new AppSettings { PolishReasoning = "none" });
        using var session = profile.Open();
        Assert.Equal("none", Control<ComboBox>(session.Window, "ReasoningBox").SelectedItem);
        Assert.False(Control<CheckBox>(session.Window, "HistoryEnabledBox").IsChecked);
    });

    [Fact] public void HistoryDisplaysOriginalMetadataAndRestoresPreviousTextWithoutCallingAProvider() => fixture.Run(() => {
        using var profile = new TestProfile();
        var store = new TranscriptHistoryStore(profile.HistoryPath);
        store.Add("First completed version", "Clean", rawText: "original UNIQUE words", provider: "Synthetic", model: "fixture-v1",
            reasoning: "low", deliveryStatus: "Copy available", durationMs: 6500);
        var id = Assert.Single(store.Entries).Id;
        store.Revise(id, "Revised completed version", "fixture-v2", "Re-polish", "Synthetic", "high");
        using var session = profile.Open();
        var window = session.Window;
        Control<TextBox>(window, "HistorySearchBox").Text = "unique";
        var list = Control<ListBox>(window, "HistoryList");
        Assert.Single(list.Items); list.SelectedIndex = 0;
        Assert.Equal("Revised completed version", Control<TextBox>(window, "HistoryDetail").Text);
        Assert.Equal("original UNIQUE words", Control<TextBox>(window, "HistoryRawDetail").Text);
        Assert.Contains("fixture-v2", Control<TextBlock>(window, "HistoryMetadata").Text);
        Assert.Contains("Copy available", Control<TextBlock>(window, "HistoryMetadata").Text);
        Assert.Contains("00:06", Control<TextBlock>(window, "HistoryMetadata").Text);
        Assert.True(Control<Button>(window, "RestoreHistoryButton").IsEnabled);
        Assert.False(Control<Button>(window, "ListenHistoryButton").IsEnabled);
        Assert.False(Control<Button>(window, "RetranscribeHistoryButton").IsEnabled);
        Click(Control<Button>(window, "RestoreHistoryButton"));
        Assert.Equal("First completed version", Control<TextBox>(window, "HistoryDetail").Text);
        Assert.Equal("original UNIQUE words", Control<TextBox>(window, "HistoryRawDetail").Text);
        Assert.False(Control<Button>(window, "RestoreHistoryButton").IsEnabled);
        store.Load();
        Assert.Equal("First completed version", Assert.Single(store.Entries).Text);
        Assert.Empty(store.Entries[0].Revisions);
    });

    [Fact] public void HistoryPreviewRequiresMatchingSelectionAndExplicitApplyAndSupportsDiscard() => fixture.Run(() => {
        using var profile = new TestProfile();
        var store = new TranscriptHistoryStore(profile.HistoryPath);
        store.Add("First original", "Clean", rawText: "first raw"); var first = store.Entries[0].Id;
        store.Add("Second original", "Clean", rawText: "second raw"); var second = store.Entries[0].Id;
        using var session = profile.Open();
        var window = session.Window; var list = Control<ListBox>(window, "HistoryList");
        list.SelectedItem = list.Items.Cast<TranscriptEntry>().Single(entry => entry.Id == first);
        // Inject only a synthetic completed preview at the actual UI routing boundary;
        // provider transports, downloads, playback and clipboard input are never invoked.
        ShowSyntheticPreview(window, second, "Wrong transcript");
        Assert.Equal(Visibility.Collapsed, Control<Border>(window, "HistoryPreviewPanel").Visibility);
        ShowSyntheticPreview(window, first, "Preview text");
        Assert.Equal(Visibility.Visible, Control<Border>(window, "HistoryPreviewPanel").Visibility);
        store.Load(); Assert.Equal("First original", store.Entries.Single(entry => entry.Id == first).Text);
        Control<TextBox>(window, "HistoryPreviewText").Text = "Reviewed final text";
        Click(ButtonWithContent(window, "Apply new version"));
        store.Load();
        var saved = store.Entries.Single(entry => entry.Id == first);
        Assert.Equal("Reviewed final text", saved.Text); Assert.Equal("first raw", saved.RawText);
        Assert.Equal("First original", Assert.Single(saved.Revisions).Text);
        Assert.Equal(Visibility.Collapsed, Control<Border>(window, "HistoryPreviewPanel").Visibility);
        ShowSyntheticPreview(window, first, "Discard this preview");
        Click(ButtonWithContent(window, "Discard preview"));
        store.Load(); Assert.Equal("Reviewed final text", store.Entries.Single(entry => entry.Id == first).Text);
        ShowSyntheticPreview(window, first, "Must not update another selection");
        list.SelectedItem = list.Items.Cast<TranscriptEntry>().Single(entry => entry.Id == second);
        Assert.Equal(Visibility.Collapsed, Control<Border>(window, "HistoryPreviewPanel").Visibility);
        Click(ButtonWithContent(window, "Apply new version"));
        store.Load();
        Assert.Equal("Second original", store.Entries.Single(entry => entry.Id == second).Text);
        Assert.Equal("Reviewed final text", store.Entries.Single(entry => entry.Id == first).Text);
    });

    private static void ShowSyntheticPreview(MainWindow window, Guid id, string text)
    {
        var previewType = typeof(MainWindow).GetNestedType("HistoryPreview", BindingFlags.NonPublic)!;
        var preview = Activator.CreateInstance(previewType, new object?[] { id, text, "fixture-model", "Re-polish", "Synthetic", "low" });
        typeof(MainWindow).GetMethod("ShowHistoryPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [preview]);
    }

    private static CheckBox CheckBoxNamed(DependencyObject root, string name)
        => LogicalDescendants<CheckBox>(root).Single(box => AutomationProperties.GetName(box) == name);

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var match in LogicalDescendants<T>(child)) yield return match;
    }

    static T Control<T>(MainWindow window, string name) where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));
    static void Click(ButtonBase button)
    {
        if (button is RadioButton radio) radio.IsChecked = true;
        else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        WpfFixture.Pump();
    }
    static void Toggle(MainWindow window, string name, bool value)
    {
        var checkbox = Control<CheckBox>(window, name);
        checkbox.IsChecked = value;
        Click(checkbox);
    }
    static Button ButtonWithContent(DependencyObject root, string content)
    {
        if (root is Button button && Equals(button.Content, content)) return button;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) {
            var match = Find(child);
            if (match != null) return match;
        }
        throw new Xunit.Sdk.XunitException($"No button has content '{content}'.");

        Button? Find(DependencyObject node)
        {
            if (node is Button candidate && Equals(candidate.Content, content)) return candidate;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) {
                var match = Find(child);
                if (match != null) return match;
            }
            return null;
        }
    }

    sealed class TestProfile : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "typr-wpf-tests-" + Guid.NewGuid().ToString("N"));
        public string SettingsDirectory => Path.Combine(root, "settings");
        public string LocalDirectory => Path.Combine(root, "local");
        public string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
        public string HistoryPath => Path.Combine(LocalDirectory, "history.json");
        public TestProfile() { Directory.CreateDirectory(SettingsDirectory); Directory.CreateDirectory(LocalDirectory); }
        public WindowSession Open() => new(new MainWindow(SettingsDirectory, LocalDirectory));
        public AppSettings ReadSettings() => SettingsMigrator.Load(File.ReadAllText(SettingsPath));
        public void WriteSettings(AppSettings settings) => File.WriteAllText(SettingsPath, SettingsMigrator.Save(settings));
        public void Dispose() => Directory.Delete(root, true);
    }
    sealed class WindowSession(MainWindow window) : IDisposable
    {
        public MainWindow Window => window;
        public void Dispose()
        {
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            WpfFixture.Pump();
            Assert.True(closed, "An idle window must close cleanly after its cleanup completes.");
        }
    }
}
