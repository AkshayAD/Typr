using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Typr.Core;
using Typr.Infrastructure;

namespace Typr.Windows;

public partial class MainWindow
{
    private AiPolishingService? _ai;
    private CancellationTokenSource? _providerLogin;
    private CancellationTokenSource? _providerComparison;
    private bool _providerBusy;
    private bool _providerRefreshRequested;
    private IReadOnlyList<PolishModel> _providerModels = [];
    private AiPolishingService Ai => _ai ??= new AiPolishingService(System.IO.Path.Combine(_localDirectory, "providers"));
    private PolishProvider PrimaryProvider => _settings.ProviderPriority.StartsWith("Claude", StringComparison.Ordinal) ? PolishProvider.Claude : PolishProvider.ChatGpt;
    private sealed record PolishOutcome(string Text, string? Provider, string? Model, string? Effort, string Note);

    private async Task<PolishOutcome> PolishTextAsync(string text, AppSettings settings, CancellationToken cancellationToken, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return new("", null, null, null, "No speech.");
        if (!settings.AiPolishEnabled && !force) return new(text, null, null, null, "AI polish is off.");
        PolishProvider primary = settings.ProviderPriority.StartsWith("Claude", StringComparison.Ordinal) ? PolishProvider.Claude : PolishProvider.ChatGpt;
        PolishProvider[] order = settings.ProviderPriority.EndsWith("first", StringComparison.Ordinal)
            ? [primary, primary == PolishProvider.ChatGpt ? PolishProvider.Claude : PolishProvider.ChatGpt] : [primary];
        string failure = "Connect a provider in Settings to enable AI polish.";
        foreach (var provider in order)
        {
            string model = provider == PolishProvider.ChatGpt ? settings.ChatGptModel : settings.ClaudeModel;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.PolishTimeoutSeconds));
            try
            {
                string result = await Ai.PolishAsync(text, provider, model, settings.PolishReasoning, timeout.Token);
                cancellationToken.ThrowIfCancellationRequested();
                return new(result, provider == PolishProvider.ChatGpt ? "ChatGPT" : "Claude", model, settings.PolishReasoning, $"Polished with {model} · {settings.PolishReasoning}.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { failure = "AI polish timed out. Your recognized words were kept."; }
            catch (PolishProviderException ex) { failure = ex.Message; }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { failure = "The selected provider couldn't finish. Reconnect or update it in Settings."; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(text, null, null, null, "Local cleanup used. " + failure);
    }

    private void ProviderPreferences_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_companionInitialized || _companionInitializing) return;
        string priority = SelectedText(ProviderPriorityBox);
        PersistSettings(_settings with { ProviderPriority = priority });
        if (ReferenceEquals(sender, ProviderPriorityBox))
        {
            _companionInitializing = true;
            _providerModels = [];
            ProviderModelBox.ItemsSource = null;
            ProviderModelBox.Text = PrimaryProvider == PolishProvider.ChatGpt ? _settings.ChatGptModel : _settings.ClaudeModel;
            ReasoningBox.ItemsSource = new[] { "none", "low", "medium", "high", "xhigh", "max" };
            ReasoningBox.SelectedItem = _settings.PolishReasoning;
            _companionInitializing = false;
            _providerLogin?.Cancel(); _providerComparison?.Cancel();
            if (_desktopIntegration) _ = RefreshProvidersAsync();
        }
        else SaveProviderSelection();
    }
    private void ProviderModel_LostFocus(object sender, RoutedEventArgs e) { if (_companionInitialized && !_companionInitializing) SaveProviderSelection(); }
    private void SaveProviderSelection()
    {
        string model = ProviderModelBox.SelectedItem is PolishModel item ? item.Id : ProviderModelBox.Text.Trim();
        if (model.Length is 0 or > 100 || !model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        { ProviderStatus.Text = "Choose a model from the refreshed list or enter its exact model ID."; return; }
        string effort = ReasoningBox.SelectedItem as string ?? _settings.PolishReasoning;
        PersistSettings(PrimaryProvider == PolishProvider.ChatGpt ? _settings with { ChatGptModel = model, PolishReasoning = effort } : _settings with { ClaudeModel = model, PolishReasoning = effort });
        if (_providerModels.FirstOrDefault(m => m.Id == model) is { } selected && !selected.ReasoningEfforts.SequenceEqual(ReasoningBox.Items.Cast<string>()))
        {
            _companionInitializing = true; ReasoningBox.ItemsSource = selected.ReasoningEfforts;
            ReasoningBox.SelectedItem = selected.ReasoningEfforts.Contains(effort) ? effort : null;
            _companionInitializing = false;
            if (ReasoningBox.SelectedItem == null) ProviderStatus.Text = "That model has different reasoning options. Choose one before dictating.";
        }
    }
    private async Task RefreshProvidersAsync()
    {
        if (_closing) return;
        if (_providerBusy) { _providerRefreshRequested = true; return; }
        _providerRefreshRequested = false;
        _providerBusy = true; SetProviderButtons(false);
        try
        {
            var provider = PrimaryProvider;
            var connection = await Ai.GetConnectionAsync(provider, _lifetime.Token);
            string? version = await Ai.GetCliVersionAsync(provider, _lifetime.Token);
            if (_closing || provider != PrimaryProvider) return;
            ProviderStatus.Text = connection.Message;
            ProviderVersion.Text = version == null ? "CLI not installed · use Install / update below" : "Installed CLI · " + version;
            _providerModels = await Ai.GetModelsAsync(provider, _lifetime.Token);
            if (_closing || provider != PrimaryProvider) return;
            _companionInitializing = true;
            string desired = provider == PolishProvider.ChatGpt ? _settings.ChatGptModel : _settings.ClaudeModel;
            ProviderModelBox.ItemsSource = _providerModels;
            ProviderModelBox.SelectedItem = _providerModels.FirstOrDefault(model => model.Id == desired);
            ProviderModelBox.Text = desired;
            var selected = _providerModels.FirstOrDefault(model => model.Id == desired);
            ReasoningBox.ItemsSource = selected?.ReasoningEfforts ?? new[] { "none", "low", "medium", "high", "xhigh", "max" };
            ReasoningBox.SelectedItem = (ReasoningBox.ItemsSource as IEnumerable<string>)!.Contains(_settings.PolishReasoning) ? _settings.PolishReasoning : null;
            _companionInitializing = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) ProviderStatus.Text = ex is PolishProviderException ? ex.Message : "Couldn't refresh the provider. Use Install / update or sign in again."; }
        finally
        {
            _providerBusy = false; _companionInitializing = false;
            if (!_closing) { SetProviderButtons(true); if (_providerRefreshRequested) _ = RefreshProvidersAsync(); }
        }
    }
    private void SetProviderButtons(bool enabled)
    {
        RefreshProviderButton.IsEnabled = ConnectProviderButton.IsEnabled = UpdateProviderButton.IsEnabled = CheckProviderUpdateButton.IsEnabled = CompareReasoningButton.IsEnabled = enabled;
        ProviderModelBox.IsEnabled = ReasoningBox.IsEnabled = enabled;
        CancelProviderButton.Visibility = !enabled && (_providerLogin != null || _providerComparison != null) ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RefreshProviders_Click(object sender, RoutedEventArgs e) => await RefreshProvidersAsync();
    private async void ConnectProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_providerBusy) return;
        _providerBusy = true;
        _providerLogin?.Cancel(); _providerLogin?.Dispose(); _providerLogin = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetProviderButtons(false);
        try
        {
            var signIn = await Ai.StartSignInAsync(PrimaryProvider, _providerLogin.Token);
            if (_closing) return;
            ProviderStatus.Text = signIn.Instructions;
            if (signIn.BrowserUri != null) Process.Start(new ProcessStartInfo(signIn.BrowserUri.AbsoluteUri) { UseShellExecute = true });
            if (signIn.LoginId != null)
            {
                var result = await Ai.WaitForSignInAsync(signIn.LoginId, _providerLogin.Token);
                if (!_closing) ProviderStatus.Text = result.Message;
            }
        }
        catch (OperationCanceledException) { if (!_closing) ProviderStatus.Text = "Sign-in didn't finish. You can try again."; }
        catch (Exception ex) { if (!_closing) ProviderStatus.Text = ex is PolishProviderException ? ex.Message : "Couldn't start sign-in. Check or update the provider CLI."; }
        finally { _providerLogin?.Dispose(); _providerLogin = null; _providerBusy = false; if (!_closing) { SetProviderButtons(true); await RefreshProvidersAsync(); } }
    }
    private async void CheckProviderUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_providerBusy) return;
        _providerBusy = true; SetProviderButtons(false);
        try
        {
            var update = await Ai.CheckCliUpdateAsync(PrimaryProvider, _lifetime.Token);
            if (!_closing) ProviderVersion.Text = update.UpdateAvailable ? $"Update available · {update.InstalledVersion ?? "not installed"} → {update.LatestVersion}" : "CLI is current · " + update.LatestVersion;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_closing) ProviderVersion.Text = "Couldn't check for updates. Check your connection and retry."; }
        finally { _providerBusy = false; if (!_closing) { SetProviderButtons(true); if (_providerRefreshRequested) await RefreshProvidersAsync(); } }
    }
    private async void UpdateProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_providerBusy || _recording || _busy) { ProviderStatus.Text = "Finish dictation before updating a provider."; return; }
        _providerBusy = true; SetProviderButtons(false);
        ProviderStatus.Text = "Installing the official CLI into Typr's own provider folder…";
        try { await Ai.InstallCliUpdateAsync(PrimaryProvider, _lifetime.Token); if (!_closing) ProviderStatus.Text = "Provider updated. Refreshing its connection and model list…"; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) ProviderStatus.Text = ex is PolishProviderException ? ex.Message : "The update couldn't finish. Your existing provider is preserved."; }
        finally { _providerBusy = false; if (!_closing) { SetProviderButtons(true); await RefreshProvidersAsync(); } }
    }

    private async void CompareReasoning_Click(object sender, RoutedEventArgs e)
    {
        if (SpeechPausedForGaming) { BenchmarkResultBox.Text = "Comparisons are paused for gaming. Choose Everyday to resume."; return; }
        if (_providerBusy || _busy || _recording) return;
        string sample = BenchmarkInputBox.Text;
        using var comparison = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _providerComparison = comparison;
        _providerBusy = true; SetProviderButtons(false); BenchmarkResultBox.Text = "Running the same sample with each supported reasoning effort…";
        try
        {
            var provider = PrimaryProvider;
            string model = provider == PolishProvider.ChatGpt ? _settings.ChatGptModel : _settings.ClaudeModel;
            var models = await Ai.GetModelsAsync(provider, comparison.Token);
            var selected = models.FirstOrDefault(m => m.Id == model) ?? throw new InvalidOperationException("Refresh models and choose an available model first.");
            var results = new List<string>();
            foreach (string effort in selected.ReasoningEfforts)
            {
                var clock = Stopwatch.StartNew();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(comparison.Token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
                string result = await Ai.PolishAsync(sample, provider, model, effort, timeout.Token);
                results.Add($"{effort.ToUpperInvariant()} · {clock.Elapsed.TotalSeconds:F2} s\n{result}");
                if (_closing) return;
                BenchmarkResultBox.Text = string.Join("\n\n──────────\n\n", results);
            }
            ProviderStatus.Text = "Comparison finished. These are single runs; network and warm-up affect time. Choose the lightest effort whose output you prefer.";
        }
        catch (OperationCanceledException) { if (!_closing) ProviderStatus.Text = "Comparison stopped or timed out. Completed results remain visible."; }
        catch (Exception ex) { if (!_closing) ProviderStatus.Text = ex.Message; }
        finally { _providerComparison = null; _providerBusy = false; if (!_closing) { SetProviderButtons(true); if (_providerRefreshRequested) await RefreshProvidersAsync(); } }
    }
    private void CancelProvider_Click(object sender, RoutedEventArgs e) { _providerLogin?.Cancel(); _providerComparison?.Cancel(); }
}
