using Microsoft.Win32;
using NAudio.CoreAudioApi;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Soundforge.Audio;
using Soundforge.Control;
using Soundforge.Models;
using Soundforge.Persistence;
using Soundforge.Settings;

namespace Soundforge;

public partial class MainWindow : Window
{
    private readonly AudioEngine _audioEngine = new();
    private readonly AudioDeviceManager _devices = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly Dictionary<Guid, TrackRow> _trackRows = new();
    private readonly Dictionary<Guid, PlaylistHeadingState> _playlistHeadings = new();
    private SoundforgeProject _project = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly LocalControlServer _controlServer;
    private Scene? _selectedScene;
    private Scene? _activeScene;
    private bool _muted;
    private bool _refreshingTrackSelectors;
    private bool _autosaveInProgress;
    private readonly bool _previousSessionEndedClean;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        _previousSessionEndedClean = _settings.LastShutdownClean;
        _settings.LastShutdownClean = false;
        _settingsStore.Save(_settings);
        _audioEngine.MasterVolume = (float)MasterSlider.Value;
        _controlServer = new LocalControlServer(command => Dispatcher.InvokeAsync(() => HandleControlCommand(command)).Task);
        _controlServer.Start();
        Loaded += async (_, _) =>
        {
            await RefreshDevicesAsync();
            TryRecoverAutosave();
            ConfigureAutosaveTimer();
        };
        AddScene("Demo");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateProgress();
        _timer.Start();

        _autosaveTimer = new DispatcherTimer();
        _autosaveTimer.Tick += async (_, _) => await RunAutosaveAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await Task.Run(_devices.GetPlaybackDevices);
            OutputDeviceCombo.ItemsSource = devices;
            OutputDeviceCombo.DisplayMemberPath = "Name";
            var preferredDeviceId = _project.PreferredOutputDeviceId ?? _settings.OutputDeviceId;
            var rememberedDevice = devices.FirstOrDefault(device => device.Id == preferredDeviceId);
            if (rememberedDevice is not null)
                OutputDeviceCombo.SelectedItem = rememberedDevice;
            else if (OutputDeviceCombo.Items.Count > 0)
                OutputDeviceCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to list audio output devices: {ex.Message}", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.aiff;*.aif;*.ogg|All files|*.*",
            Multiselect = true,
            Title = "Import audio sources"
        };

        if (dialog.ShowDialog() != true) return;

        var failures = new List<string>();
        foreach (var fileName in dialog.FileNames)
        {
            try
            {
                var track = new Track
                {
                    Name = System.IO.Path.GetFileName(fileName),
                    FilePath = fileName,
                    Volume = 0.8,
                    LayerId = _selectedScene?.Layers.FirstOrDefault()?.Id
                };

                var targetLayer = GetLayer(track.LayerId);
                track.Loop = targetLayer?.PlaybackBehavior == LayerPlaybackBehavior.Ambience;
                track.AutoPlayOnSceneActivation = targetLayer?.PlaybackBehavior != LayerPlaybackBehavior.Effects;
                AssignTrackToLayer(track, track.LayerId);
                AddTrackRow(null, track);
            }
            catch (Exception ex)
            {
                failures.Add($"{System.IO.Path.GetFileName(fileName)} — {ex.Message}");
            }
        }

        if (failures.Count > 0)
            MessageBox.Show($"Some files could not be imported:\n\n{string.Join("\n", failures)}", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var device = OutputDeviceCombo.SelectedItem as AudioDeviceInfo;
        _audioEngine.SetOutputDevice(device?.Id);
        _project.PreferredOutputDeviceId = device?.Id;
        _settings.OutputDeviceId = device?.Id;
        _settingsStore.Save(_settings);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        _audioEngine.MasterVolume = _muted ? 0f : (float)MasterSlider.Value;
        ((Button)sender).Content = _muted ? "🔊 UNMUTE ALL" : "🔇 MUTE ALL";
    }

    private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _project.MasterVolume = e.NewValue;
        if (IsInitialized && !_muted)
            _audioEngine.MasterVolume = (float)e.NewValue;
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _audioEngine.StopAll();
        RefreshTrackVisibility();
    }

    private SoundforgeControlResult HandleControlCommand(SoundforgeControlCommand command)
    {
        try
        {
            return command.Action switch
            {
                "activateScene" => ActivateSceneFromControl(command.SceneName),
                "activateMusicPool" => ActivateMusicPoolFromControl(command.SceneName, command.PoolName),
                "toggleAmbience" => ToggleLayerFromControl(LayerPlaybackBehavior.Ambience),
                "triggerEffect" => TriggerEffectFromControl(command.TrackName),
                "stopAll" => StopAllFromControl(),
                "adjustMaster" => AdjustMasterFromControl(command.Ticks),
                "adjustMusic" => AdjustLayerFromControl(LayerPlaybackBehavior.Music, command.Ticks),
                "adjustAmbience" => AdjustLayerFromControl(LayerPlaybackBehavior.Ambience, command.Ticks),
                "adjustEffects" => AdjustLayerFromControl(LayerPlaybackBehavior.Effects, command.Ticks),
                "listScenes" => new SoundforgeControlResult(true, "Scenes loaded.", _project.Scenes.Select(scene => scene.Name).Order().ToList()),
                "listMusicPools" => ListMusicPoolsFromControl(command.SceneName),
                "listEffects" => new SoundforgeControlResult(
                    true,
                    _activeScene is null ? "Activate a scene to list its effects." : "Effects loaded.",
                    _activeScene is null
                        ? []
                        : GetSceneSources(_activeScene)
                            .Where(source => source.Layer.PlaybackBehavior == LayerPlaybackBehavior.Effects)
                            .Select(source => source.Track.Name)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Order()
                            .ToList()),
                _ => new SoundforgeControlResult(false, "Soundforge does not recognise that control action.")
            };
        }
        catch (Exception ex)
        {
            return new SoundforgeControlResult(false, ex.Message);
        }
    }

    private SoundforgeControlResult ActivateSceneFromControl(string? sceneName)
    {
        var scene = _project.Scenes.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, sceneName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scene is null)
            return new SoundforgeControlResult(false, "That scene does not exist in the open project.");

        SelectScene(scene, activate: false);
        ActivateScene(scene, reportErrors: false);
        return new SoundforgeControlResult(true, $"Activated {scene.Name}.");
    }

    private SoundforgeControlResult ListMusicPoolsFromControl(string? sceneName)
    {
        var scene = _project.Scenes.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, sceneName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scene is null)
            return new SoundforgeControlResult(false, "Choose a valid scene before loading its Music pools.");

        var pools = scene.Layers
            .Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Music)
            .SelectMany(layer => layer.Playlists)
            .Select(playlist => playlist.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SoundforgeControlResult(true, "Music pools loaded.", pools);
    }

    private SoundforgeControlResult ActivateMusicPoolFromControl(string? sceneName, string? poolName)
    {
        var scene = _project.Scenes.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, sceneName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scene is null)
            return new SoundforgeControlResult(false, "That scene does not exist in the open project.");

        var target = scene.Layers
            .Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Music)
            .SelectMany(layer => layer.Playlists.Select(playlist => (layer, playlist)))
            .FirstOrDefault(candidate => string.Equals(candidate.playlist.Name, poolName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target.playlist is null)
            return new SoundforgeControlResult(false, "That Music pool does not exist in the selected scene.");

        var sceneWasActive = _activeScene == scene;
        SelectScene(scene, activate: false);
        var previousPoolId = target.layer.ActivePlaylistId;
        try
        {
            ActivatePlaylistPool(target.layer, target.playlist, reportErrors: false);
            if (!sceneWasActive)
                ActivateScene(scene, reportErrors: false);
        }
        catch
        {
            target.layer.ActivePlaylistId = previousPoolId;
            RenderLayers();
            RefreshTrackVisibility();
            throw;
        }
        return new SoundforgeControlResult(true, $"Activated {scene.Name} / {target.playlist.Name}.");
    }

    private SoundforgeControlResult ToggleLayerFromControl(LayerPlaybackBehavior behavior)
    {
        if (_activeScene is null)
            return new SoundforgeControlResult(false, "Activate a scene before controlling its layers.");

        var sources = GetSceneSources(_activeScene)
            .Where(source => source.Layer.PlaybackBehavior == behavior)
            .ToList();
        if (sources.Count == 0)
            return new SoundforgeControlResult(false, $"The active scene has no {behavior} sources.");

        var sourceIds = sources.Select(source => source.Track.Id).ToHashSet();
        var activeSessions = _audioEngine.GetSessions()
            .Where(session => sourceIds.Contains(session.TrackId))
            .ToList();
        if (activeSessions.Count > 0)
        {
            foreach (var session in activeSessions)
                _audioEngine.Stop(session.SessionId, GetFadeOutDuration(_activeScene));
            return new SoundforgeControlResult(true, $"Stopped {behavior}.");
        }

        var tracks = behavior == LayerPlaybackBehavior.Ambience
            ? GetAutoAmbienceTracks(_activeScene).ToList()
            : sources.Select(source => source.Track).ToList();
        if (tracks.Count == 0)
            return new SoundforgeControlResult(false, "Mark at least one ambience source as Auto Play before using this control.");

        foreach (var track in tracks.GroupBy(track => track.Id).Select(group => group.First()))
            StartTrackFromControl(track);
        return new SoundforgeControlResult(true, $"Started {behavior}.");
    }

    private SoundforgeControlResult TriggerEffectFromControl(string? trackName)
    {
        if (_activeScene is null)
            return new SoundforgeControlResult(false, "Activate a scene before triggering an effect.");

        var track = GetSceneSources(_activeScene)
            .Where(source => source.Layer.PlaybackBehavior == LayerPlaybackBehavior.Effects)
            .Select(source => source.Track)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, trackName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (track is null)
            return new SoundforgeControlResult(false, "That effect source does not exist in the active scene.");

        StartTrackFromControl(track);
        return new SoundforgeControlResult(true, $"Triggered {track.Name}.");
    }

    private SoundforgeControlResult StopAllFromControl()
    {
        _audioEngine.StopAll();
        RefreshTrackVisibility();
        return new SoundforgeControlResult(true, "Stopped all sources.");
    }

    private SoundforgeControlResult AdjustMasterFromControl(int ticks)
    {
        _muted = false;
        MasterSlider.Value = Math.Clamp(MasterSlider.Value + ticks * 0.02, MasterSlider.Minimum, MasterSlider.Maximum);
        return new SoundforgeControlResult(true, $"Master volume {MasterSlider.Value:P0}.", Level: MasterSlider.Value);
    }

    private SoundforgeControlResult AdjustLayerFromControl(LayerPlaybackBehavior behavior, int ticks)
    {
        if (_activeScene is null)
            return new SoundforgeControlResult(false, "Activate a scene before controlling its layers.");

        var layers = _activeScene.Layers.Where(layer => layer.PlaybackBehavior == behavior).ToList();
        if (layers.Count == 0)
            return new SoundforgeControlResult(false, $"The active scene has no {behavior} layer.");

        foreach (var layer in layers)
        {
            layer.Volume = Math.Clamp(layer.Volume + ticks * 0.02, 0, 1);
            ApplyLayerMix(layer.Id);
        }
        RenderMixer();
        return new SoundforgeControlResult(true, $"{behavior} volume adjusted.", Level: layers[0].Volume);
    }

    private void StartTrackFromControl(Track track)
    {
        var sessionId = _audioEngine.Start(track, GetFadeInDuration(_activeScene));
        AddTrackRow(sessionId, track);
        ApplyLayerMix(track.LayerId);
        EmptyTracksText.Visibility = Visibility.Collapsed;
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Soundforge project|*.soundforge|All files|*.*",
            DefaultExt = ".soundforge",
            FileName = $"{_project.Name}.soundforge"
        };

        if (dialog.ShowDialog() != true)
            return;

        var progressWindow = CreateProjectProgressWindow("Saving Soundforge project");
        try
        {
            _project.MasterVolume = MasterSlider.Value;
            var progress = new Progress<ProjectStoreProgress>(progressWindow.ShowProgress);
            await Task.Run(() => SoundforgeProjectStore.Save(dialog.FileName, _project, progress));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to save project: {ex.Message}", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CloseProjectProgressWindow(progressWindow);
        }
    }

    private void AutosaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new AutosaveSettingsWindow(_settings, GetDefaultAutosaveFolder()) { Owner = this };
        if (window.ShowDialog() != true)
            return;

        _settings.AutosaveEnabled = window.AutosaveEnabled;
        _settings.AutosaveIntervalMinutes = window.AutosaveIntervalMinutes;
        _settings.AutosaveFolder = window.AutosaveFolder;
        _settingsStore.Save(_settings);
        ConfigureAutosaveTimer();

        if (_settings.AutosaveEnabled)
            _ = RunAutosaveAsync();
        else
            AutosaveStatusText.Text = "Autosave: Off";
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Soundforge project|*.soundforge|All files|*.*",
            DefaultExt = ".soundforge"
        };

        if (dialog.ShowDialog() != true)
            return;

        var progressWindow = CreateProjectProgressWindow("Loading Soundforge project");
        _audioEngine.StopAll();
        try
        {
            var progress = new Progress<ProjectStoreProgress>(progressWindow.ShowProgress);
            var loadedProject = await Task.Run(() => SoundforgeProjectStore.Load(dialog.FileName, progress));
            progressWindow.ShowFinalizing("Preparing scene editor…");
            await Dispatcher.Yield(DispatcherPriority.Render);
            ApplyLoadedProject(loadedProject);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to open project: {ex.Message}", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CloseProjectProgressWindow(progressWindow);
        }
    }

    private ProjectProgressWindow CreateProjectProgressWindow(string heading)
    {
        var window = new ProjectProgressWindow(heading) { Owner = this };
        window.Show();
        IsEnabled = false;
        return window;
    }

    private void ApplyLoadedProject(SoundforgeProject loadedProject)
    {
        _trackRows.Clear();
        ActiveTracksPanel.Children.Clear();
        _project = loadedProject;
        NormalizePlaylistPools(_project);
        _activeScene = null;
        _selectedScene = null;
        _muted = false;
        MasterSlider.Value = _project.MasterVolume;

        var savedOutputDevice = (OutputDeviceCombo.ItemsSource as IEnumerable<AudioDeviceInfo>)
            ?.FirstOrDefault(device => device.Id == _project.PreferredOutputDeviceId);
        if (savedOutputDevice is not null)
            OutputDeviceCombo.SelectedItem = savedOutputDevice;

        if (_project.Scenes.Count == 0)
        {
            AddScene("Demo");
            return;
        }

        foreach (var track in _project.Scenes
            .SelectMany(scene => scene.Layers)
            .SelectMany(layer => layer.Playlists)
            .SelectMany(playlist => playlist.Tracks)
            .GroupBy(track => track.Id)
            .Select(group => group.First()))
        {
            AddTrackRow(null, track);
        }

        SelectScene(_project.Scenes[0], activate: false);
        CurrentSceneText.Text = "ACTIVE SCENE: NONE";
    }

    private void ConfigureAutosaveTimer()
    {
        _autosaveTimer.Stop();
        if (!_settings.AutosaveEnabled)
        {
            AutosaveStatusText.Text = "Autosave: Off";
            return;
        }

        _settings.AutosaveIntervalMinutes = Math.Clamp(_settings.AutosaveIntervalMinutes, 1, 60);
        _autosaveTimer.Interval = TimeSpan.FromMinutes(_settings.AutosaveIntervalMinutes);
        _autosaveTimer.Start();
        AutosaveStatusText.Text = $"Autosave: On · {_settings.AutosaveIntervalMinutes} min";
    }

    private async Task RunAutosaveAsync()
    {
        if (!_settings.AutosaveEnabled || _autosaveInProgress)
            return;

        _autosaveInProgress = true;
        try
        {
            AutosaveStatusText.Text = "Autosave: Saving…";
            _project.MasterVolume = MasterSlider.Value;
            var projectJson = SoundforgeProjectStore.SerializeRecovery(_project);
            var autosavePath = GetAutosavePath();
            await Task.Run(() => SoundforgeProjectStore.WriteRecovery(autosavePath, projectJson));
            AutosaveStatusText.Text = $"Autosaved {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            AutosaveStatusText.Text = "Autosave: Failed";
            AutosaveStatusText.ToolTip = ex.Message;
        }
        finally
        {
            _autosaveInProgress = false;
        }
    }

    private void TryRecoverAutosave()
    {
        var autosavePath = GetAutosavePath();
        if (_previousSessionEndedClean)
        {
            TryDeleteAutosave(autosavePath);
            return;
        }

        if (!_settings.AutosaveEnabled || !File.Exists(autosavePath))
            return;

        var savedAt = File.GetLastWriteTime(autosavePath);
        var choice = MessageBox.Show(
            $"Soundforge did not close normally last time.\n\nRestore the autosave from {savedAt:g}?",
            "Recover Soundforge project",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (choice != MessageBoxResult.Yes)
        {
            TryDeleteAutosave(autosavePath);
            return;
        }

        try
        {
            _audioEngine.StopAll();
            ApplyLoadedProject(SoundforgeProjectStore.Load(autosavePath));
            AutosaveStatusText.Text = $"Recovered autosave from {savedAt:HH:mm}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Soundforge could not recover the autosave:\n\n{ex.Message}",
                "Autosave recovery",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string GetAutosavePath()
    {
        var folder = string.IsNullOrWhiteSpace(_settings.AutosaveFolder)
            ? GetDefaultAutosaveFolder()
            : _settings.AutosaveFolder;
        return Path.Combine(folder, "Soundforge-recovery.autosave.json");
    }

    private static string GetDefaultAutosaveFolder() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Soundforge-Autosaves"));

    private static void TryDeleteAutosave(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale recovery file is harmless; the next successful autosave replaces it atomically.
        }
    }

    private void CloseProjectProgressWindow(ProjectProgressWindow window)
    {
        IsEnabled = true;
        window.FinishAndClose();
        Activate();
    }

    private void AddScene_Click(object sender, RoutedEventArgs e)
    {
        var name = NewSceneNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Enter a name for the new scene.", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_project.Scenes.Any(scene => string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("A scene with that name already exists.", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddScene(name);
        NewSceneNameText.Clear();
    }

    private void MoveSceneUp_Click(object sender, RoutedEventArgs e) => MoveSelectedScene(-1);

    private void MoveSceneDown_Click(object sender, RoutedEventArgs e) => MoveSelectedScene(1);

    private void MoveSelectedScene(int offset)
    {
        if (_selectedScene is null || !SceneOrdering.Move(_project.Scenes, _selectedScene, offset))
            return;

        RefreshSceneButtons();
    }

    private void SortScenes_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Scenes.Count < 2)
            return;

        var choice = MessageBox.Show(
            "Sort every scene alphabetically from A to Z?\n\nThis changes the scene order saved with the project.",
            "Sort Scenes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes)
            return;

        SceneOrdering.SortByName(_project.Scenes);
        RefreshSceneButtons();
    }

    private void SaveSceneName_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScene is null)
            return;

        var name = SelectedSceneNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("A scene needs a name.", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_project.Scenes.Any(scene => scene != _selectedScene && string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("A scene with that name already exists.", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectedScene.Name = name;
        RefreshSceneEditor();
    }

    private void DeleteScene_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScene is null)
            return;

        if (_project.Scenes.Count == 1)
        {
            MessageBox.Show("Keep at least one scene in the project.", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Delete '{_selectedScene.Name}'?", "Soundforge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var deletedActiveScene = _selectedScene == _activeScene;
        _project.Scenes.Remove(_selectedScene);
        var nextScene = _project.Scenes[0];
        SelectScene(nextScene);
        if (deletedActiveScene)
            ActivateScene(nextScene);
    }

    private void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScene is null)
            return;

        var name = NewLayerNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Enter a name for the new layer.", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectedScene.Layers.Add(CreateLayer(name, LayerPlaybackBehavior.Manual, $"{name} Pool"));
        NewLayerNameText.Clear();
        RenderLayers();
        RefreshTrackLayerSelectors();
    }

    private void ShowSceneEditor_Click(object sender, RoutedEventArgs e)
    {
        SceneEditorPage.Visibility = Visibility.Visible;
        MixerPage.Visibility = Visibility.Collapsed;
    }

    private void ShowMixer_Click(object sender, RoutedEventArgs e)
    {
        RenderMixer();
        SceneEditorPage.Visibility = Visibility.Collapsed;
        MixerPage.Visibility = Visibility.Visible;
    }

    private void TransitionSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectedScene is not null)
            _selectedScene.UseCrossfade = SceneCrossfadeCheckBox.IsChecked == true;
    }

    private void TrackViewMode_Changed(object sender, RoutedEventArgs e) => RefreshTrackVisibility();

    private void TransitionDuration_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedScene is null)
            return;

        if (double.TryParse(FadeInSecondsText.Text, out var fadeIn) && fadeIn >= 0)
            _selectedScene.FadeInSeconds = fadeIn;
        else
            FadeInSecondsText.Text = _selectedScene.FadeInSeconds.ToString("0.##");

        if (double.TryParse(FadeOutSecondsText.Text, out var fadeOut) && fadeOut >= 0)
            _selectedScene.FadeOutSeconds = fadeOut;
        else
            FadeOutSecondsText.Text = _selectedScene.FadeOutSeconds.ToString("0.##");
    }

    private void AddScene(string name)
    {
        var scene = new Scene
        {
            Name = name,
            Layers = new List<Layer>
            {
                CreateLayer("Music", LayerPlaybackBehavior.Music, "Main Pool", isDefault: true),
                CreateLayer("Ambience", LayerPlaybackBehavior.Ambience, "Ambience Pool", isDefault: true),
                CreateLayer("Effects", LayerPlaybackBehavior.Effects, "Effects Pool", isDefault: true)
            }
        };

        _project.Scenes.Add(scene);
        SelectScene(scene);
    }

    private void SelectScene(Scene scene, bool activate = true)
    {
        _selectedScene = scene;
        RefreshSceneEditor();
        if (activate && EditModeCheckBox.IsChecked != true)
            ActivateScene(scene);
    }

    private void ActivateSelectedScene_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScene is not null)
            ActivateScene(_selectedScene);
    }

    private void RefreshSceneEditor()
    {
        if (_selectedScene is null)
            return;

        SelectedSceneNameText.Text = _selectedScene.Name;
        SceneCrossfadeCheckBox.IsChecked = _selectedScene.UseCrossfade;
        FadeInSecondsText.Text = _selectedScene.FadeInSeconds.ToString("0.##");
        FadeOutSecondsText.Text = _selectedScene.FadeOutSeconds.ToString("0.##");
        RefreshSceneButtons();
        RenderLayers();
        RefreshTrackVisibility();
        RenderMixer();
    }

    private void RefreshSceneButtons()
    {
        SceneButtonsPanel.Children.Clear();
        foreach (var scene in _project.Scenes)
        {
            var button = new Button
            {
                Content = scene.Name,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 3, 0, 0),
                FontWeight = scene == _selectedScene ? FontWeights.SemiBold : FontWeights.Normal
            };
            if (scene == _activeScene)
                button.Content = $"▶ {scene.Name}";
            button.Click += (_, _) => SelectScene(scene);
            SceneButtonsPanel.Children.Add(button);
        }

        var selectedIndex = _selectedScene is null ? -1 : _project.Scenes.IndexOf(_selectedScene);
        MoveSceneUpButton.IsEnabled = selectedIndex > 0;
        MoveSceneDownButton.IsEnabled = selectedIndex >= 0 && selectedIndex < _project.Scenes.Count - 1;
    }

    private void RenderLayers()
    {
        LayerPanel.Children.Clear();
        if (_selectedScene is null)
            return;

        foreach (var layer in _selectedScene.Layers.ToList())
        {
            EnsureLayerHasPlaylist(layer);
            var name = new TextBox
            {
                Text = layer.Name,
                Height = 28,
                MinWidth = 180,
                IsReadOnly = layer.IsDefault,
                ToolTip = layer.IsDefault ? "Default layer names are locked" : null
            };
            var remove = new Button
            {
                Content = layer.IsDefault ? "Locked" : "Remove",
                IsEnabled = !layer.IsDefault,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(8, 0, 0, 0)
            };
            var behavior = new ComboBox
            {
                ItemsSource = Enum.GetValues<LayerPlaybackBehavior>(),
                SelectedItem = layer.PlaybackBehavior,
                Width = 105,
                Height = 28,
                IsEnabled = !layer.IsDefault,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = layer.IsDefault ? "Default layer types are locked" : "Layer type"
            };
            name.TextChanged += (_, _) =>
            {
                if (!layer.IsDefault)
                    layer.Name = name.Text.Trim();
            };
            name.LostFocus += (_, _) =>
            {
                RefreshTrackLayerSelectors();
                RefreshTrackVisibility();
            };
            remove.Click += (_, _) =>
            {
                if (layer.IsDefault)
                    return;
                _selectedScene.Layers.Remove(layer);
                foreach (var trackRow in _trackRows.Values.Where(trackRow => trackRow.Track.LayerId == layer.Id))
                    trackRow.Track.LayerId = null;
                RenderLayers();
                RefreshTrackLayerSelectors();
                RefreshTrackVisibility();
            };
            behavior.SelectionChanged += (_, _) =>
            {
                if (behavior.SelectedItem is not LayerPlaybackBehavior selectedBehavior || selectedBehavior == layer.PlaybackBehavior)
                    return;

                layer.PlaybackBehavior = selectedBehavior;
                foreach (var track in layer.Playlists.SelectMany(playlist => playlist.Tracks))
                    ApplyLayerPlaybackDefaults(track);
                RenderLayers();
                RefreshTrackLayerSelectors();
                RefreshTrackVisibility();
            };

            var row = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(behavior, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(behavior);
            row.Children.Add(name);

            var playlistPanel = new StackPanel { Margin = new Thickness(10, 8, 0, 0) };
            playlistPanel.Children.Add(new TextBlock
            {
                Text = layer.PlaybackBehavior == LayerPlaybackBehavior.Music ? "MUSIC PLAYLIST POOLS — choose one active pool" : "PLAYLIST POOLS",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(159, 208, 255)),
                Margin = new Thickness(0, 0, 0, 3)
            });

            foreach (var playlist in layer.Playlists.ToList())
            {
                var poolName = new TextBox { Text = playlist.Name, Height = 26, MinWidth = 180 };
                var poolCount = new TextBlock
                {
                    Text = $"{playlist.Tracks.Count} source{(playlist.Tracks.Count == 1 ? "" : "s")}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(200, 203, 211))
                };
                var deletePool = new Button { Content = "Delete Pool", Padding = new Thickness(7, 2, 7, 2) };
                var poolRow = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                DockPanel.SetDock(deletePool, Dock.Right);
                DockPanel.SetDock(poolCount, Dock.Right);
                poolRow.Children.Add(deletePool);
                poolRow.Children.Add(poolCount);

                if (layer.PlaybackBehavior == LayerPlaybackBehavior.Music)
                {
                    var active = new RadioButton
                    {
                        GroupName = $"ActivePool-{layer.Id}",
                        IsChecked = layer.ActivePlaylistId == playlist.Id,
                        ToolTip = "Use this playlist when the scene is active",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    active.Checked += (_, _) => ActivatePlaylistPool(layer, playlist);
                    DockPanel.SetDock(active, Dock.Left);
                    poolRow.Children.Add(active);
                }

                poolName.TextChanged += (_, _) => playlist.Name = poolName.Text.Trim();
                poolName.LostFocus += (_, _) =>
                {
                    RefreshTrackLayerSelectors();
                    RefreshTrackVisibility();
                    RenderQuickMusicPools();
                };
                deletePool.Click += (_, _) => DeletePlaylistPool(layer, playlist);
                poolRow.Children.Add(poolName);
                playlistPanel.Children.Add(poolRow);

                if (layer.PlaybackBehavior == LayerPlaybackBehavior.Ambience)
                {
                    var links = new WrapPanel { Margin = new Thickness(22, 3, 0, 3) };
                    links.Children.Add(new TextBlock
                    {
                        Text = "AUTO WITH:",
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    });
                    var musicPools = _selectedScene.Layers
                        .Where(candidate => candidate.PlaybackBehavior == LayerPlaybackBehavior.Music)
                        .SelectMany(candidate => candidate.Playlists.Select(pool => (candidate, pool)))
                        .ToList();
                    if (musicPools.Count == 0)
                    {
                        links.Children.Add(new TextBlock { Text = "No Music pools available", Foreground = Brushes.Gray });
                    }
                    else
                    {
                        foreach (var (musicLayer, musicPool) in musicPools)
                        {
                            var link = new CheckBox
                            {
                                Content = musicLayer.Name == "Music" ? musicPool.Name : $"{musicLayer.Name} / {musicPool.Name}",
                                IsChecked = playlist.AutoActivateWithMusicPlaylistIds.Contains(musicPool.Id),
                                Margin = new Thickness(0, 0, 12, 0),
                                ToolTip = "Automatically activate this Ambience pool with the selected Music pool"
                            };
                            link.Checked += (_, _) =>
                            {
                                if (!playlist.AutoActivateWithMusicPlaylistIds.Contains(musicPool.Id))
                                    playlist.AutoActivateWithMusicPlaylistIds.Add(musicPool.Id);
                                if (_activeScene == _selectedScene)
                                    SyncLinkedAmbience(_selectedScene);
                            };
                            link.Unchecked += (_, _) =>
                            {
                                playlist.AutoActivateWithMusicPlaylistIds.Remove(musicPool.Id);
                                if (_activeScene == _selectedScene)
                                    SyncLinkedAmbience(_selectedScene);
                            };
                            links.Children.Add(link);
                        }
                    }
                    playlistPanel.Children.Add(links);
                }
            }

            var newPoolName = new TextBox { Height = 26, MinWidth = 180, ToolTip = "New playlist pool name" };
            var addPool = new Button { Content = "＋ Add Pool", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0) };
            addPool.Click += (_, _) =>
            {
                var poolName = newPoolName.Text.Trim();
                if (string.IsNullOrWhiteSpace(poolName))
                    return;

                var playlist = new Playlist { Name = poolName };
                layer.Playlists.Add(playlist);
                layer.ActivePlaylistId ??= playlist.Id;
                RenderLayers();
                RefreshTrackLayerSelectors();
                RefreshTrackVisibility();
            };
            var addPoolRow = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
            DockPanel.SetDock(addPool, Dock.Right);
            addPoolRow.Children.Add(addPool);
            addPoolRow.Children.Add(newPoolName);
            playlistPanel.Children.Add(addPoolRow);

            var layerPanel = new StackPanel();
            layerPanel.Children.Add(row);
            layerPanel.Children.Add(playlistPanel);
            LayerPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(25, 27, 32)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(55, 60, 70)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 6, 0, 0),
                Child = layerPanel
            });
        }
        RenderQuickMusicPools();
    }

    private void RenderQuickMusicPools()
    {
        QuickMusicPoolPanel.Children.Clear();
        if (_selectedScene is null)
            return;

        var musicLayers = _selectedScene.Layers.Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Music).ToList();
        foreach (var layer in musicLayers)
        {
            foreach (var playlist in layer.Playlists)
            {
                var isActive = layer.ActivePlaylistId == playlist.Id;
                var button = new Button
                {
                    Content = $"{(isActive ? "●" : "○")} {(musicLayers.Count > 1 ? $"{layer.Name} / " : "")}{playlist.Name}",
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 8, 5),
                    FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
                    Background = isActive
                        ? new SolidColorBrush(Color.FromRgb(43, 111, 173))
                        : new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    Foreground = isActive ? Brushes.White : Brushes.Black,
                    ToolTip = isActive ? "Active Music pool" : $"Switch to {playlist.Name}"
                };
                button.Click += (_, _) => ActivatePlaylistPool(layer, playlist);
                QuickMusicPoolPanel.Children.Add(button);
            }
        }

        if (QuickMusicPoolPanel.Children.Count == 0)
            QuickMusicPoolPanel.Children.Add(new TextBlock { Text = "No Music pools in the selected scene.", Foreground = Brushes.Gray });
    }

    private static Layer CreateLayer(string name, LayerPlaybackBehavior behavior, string playlistName, bool isDefault = false)
    {
        var playlist = new Playlist { Name = playlistName };
        return new Layer
        {
            Name = name,
            IsDefault = isDefault,
            PlaybackBehavior = behavior,
            ActivePlaylistId = playlist.Id,
            Playlists = [playlist]
        };
    }

    private static void EnsureLayerHasPlaylist(Layer layer)
    {
        foreach (var playlist in layer.Playlists)
        {
            if (playlist.Id == Guid.Empty)
                playlist.Id = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(playlist.Name))
                playlist.Name = "Playlist Pool";
            foreach (var track in playlist.Tracks)
                track.LayerId = layer.Id;
        }

        if (layer.Playlists.Count == 0)
            layer.Playlists.Add(new Playlist { Name = $"{layer.Name} Pool" });

        if (layer.ActivePlaylistId is not Guid activeId || layer.Playlists.All(playlist => playlist.Id != activeId))
            layer.ActivePlaylistId = layer.Playlists[0].Id;
    }

    private static void NormalizePlaylistPools(SoundforgeProject project)
    {
        project.FormatVersion = Math.Max(project.FormatVersion, 5);
        foreach (var scene in project.Scenes)
        {
            EnsureDefaultSceneLayers(scene);
            foreach (var layer in scene.Layers)
                EnsureLayerHasPlaylist(layer);

            var validMusicPoolIds = scene.Layers
                .Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Music)
                .SelectMany(layer => layer.Playlists)
                .Select(playlist => playlist.Id)
                .ToHashSet();
            foreach (var ambiencePool in scene.Layers
                         .Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Ambience)
                         .SelectMany(layer => layer.Playlists))
            {
                ambiencePool.AutoActivateWithMusicPlaylistIds.RemoveAll(id => !validMusicPoolIds.Contains(id));
            }
        }
    }

    private static void EnsureDefaultSceneLayers(Scene scene)
    {
        var definitions = new[]
        {
            (Name: "Music", Behavior: LayerPlaybackBehavior.Music, PoolName: "Main Pool"),
            (Name: "Ambience", Behavior: LayerPlaybackBehavior.Ambience, PoolName: "Ambience Pool"),
            (Name: "Effects", Behavior: LayerPlaybackBehavior.Effects, PoolName: "Effects Pool")
        };
        var defaults = new List<Layer>();
        var claimedIds = new HashSet<Guid>();
        foreach (var definition in definitions)
        {
            var layer = scene.Layers.FirstOrDefault(candidate =>
                            !claimedIds.Contains(candidate.Id)
                            && candidate.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase))
                        ?? scene.Layers.FirstOrDefault(candidate =>
                            !claimedIds.Contains(candidate.Id)
                            && candidate.PlaybackBehavior == definition.Behavior)
                        ?? CreateLayer(definition.Name, definition.Behavior, definition.PoolName, isDefault: true);

            if (!scene.Layers.Contains(layer))
                scene.Layers.Add(layer);
            layer.Name = definition.Name;
            layer.PlaybackBehavior = definition.Behavior;
            layer.IsDefault = true;
            claimedIds.Add(layer.Id);
            defaults.Add(layer);
        }

        var customLayers = scene.Layers.Where(layer => !claimedIds.Contains(layer.Id)).ToList();
        foreach (var customLayer in customLayers)
            customLayer.IsDefault = false;
        scene.Layers = defaults.Concat(customLayers).ToList();
    }

    private bool RunPlaybackAction(Action action, bool reportErrors = true)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (reportErrors)
        {
            MessageBox.Show(this, ex.Message, "Unable to play source", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ActivatePlaylistPool(Layer layer, Playlist playlist, bool reportErrors = true)
    {
        var isLiveMusic = _activeScene == _selectedScene && _activeScene?.Layers.Contains(layer) == true
            && layer.PlaybackBehavior == LayerPlaybackBehavior.Music;
        if (layer.ActivePlaylistId == playlist.Id && (!isLiveMusic || _audioEngine.GetSessions()
                .Any(session => playlist.Tracks.Any(track => track.Id == session.TrackId))))
            return;

        RunPlaybackAction(() =>
        {
            var previousId = layer.ActivePlaylistId;
            layer.ActivePlaylistId = playlist.Id;
            try
            {
                if (isLiveMusic)
                    SwitchLiveMusicPool(layer, playlist);
            }
            catch
            {
                layer.ActivePlaylistId = previousId;
                throw;
            }
            finally
            {
                RenderLayers();
                RefreshTrackLayerSelectors();
                RefreshTrackVisibility();
            }
        }, reportErrors);
    }

    private void SwitchLiveMusicPool(Layer layer, Playlist playlist)
    {
        if (_activeScene is null)
            return;

        var targets = GetAutoAmbienceTracks(_activeScene).ToList();
        if (playlist.Tracks.Count > 0)
            targets.Add(playlist.Tracks[Random.Shared.Next(playlist.Tracks.Count)]);

        var affectedLayers = _activeScene.Layers
            .Where(candidate => candidate.Id == layer.Id || candidate.PlaybackBehavior == LayerPlaybackBehavior.Ambience)
            .Select(candidate => candidate.Id).ToHashSet();
        var sessions = _audioEngine.GetSessions().Where(session =>
            _trackRows.TryGetValue(session.TrackId, out var row) && row.Track.LayerId is Guid id && affectedLayers.Contains(id));
        TransitionTracks(targets, sessions, _activeScene);
    }

    private void DeletePlaylistPool(Layer layer, Playlist playlist)
    {
        if (layer.Playlists.Count == 1)
        {
            MessageBox.Show("Each layer needs at least one playlist pool.", "Soundforge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fallback = layer.Playlists.First(candidate => candidate.Id != playlist.Id);
        foreach (var track in playlist.Tracks)
        {
            if (fallback.Tracks.All(existing => existing.Id != track.Id))
                fallback.Tracks.Add(track);
        }

        layer.Playlists.Remove(playlist);
        foreach (var ambiencePool in _selectedScene?.Layers
                     .Where(candidate => candidate.PlaybackBehavior == LayerPlaybackBehavior.Ambience)
                     .SelectMany(candidate => candidate.Playlists) ?? [])
        {
            ambiencePool.AutoActivateWithMusicPlaylistIds.Remove(playlist.Id);
        }
        if (layer.ActivePlaylistId == playlist.Id)
        {
            layer.ActivePlaylistId = fallback.Id;
            if (_activeScene == _selectedScene && layer.PlaybackBehavior == LayerPlaybackBehavior.Music)
                RunPlaybackAction(() => SwitchLiveMusicPool(layer, fallback));
        }

        RenderLayers();
        RefreshTrackLayerSelectors();
        RefreshTrackVisibility();
    }

    private void RenderMixer()
    {
        if (MixerLayerPanel is null || MixerTitleText is null)
            return;

        MixerLayerPanel.Children.Clear();
        if (_selectedScene is null)
        {
            MixerTitleText.Text = "SCENE MIXER";
            return;
        }

        MixerTitleText.Text = $"SCENE MIXER: {_selectedScene.Name.ToUpperInvariant()}";
        foreach (var layer in _selectedScene.Layers)
        {
            var sourceCount = layer.Playlists.Sum(playlist => playlist.Tracks.Count);
            var activeCount = _audioEngine.GetSessions().Count(session =>
                _trackRows.TryGetValue(session.TrackId, out var trackRow) && trackRow.Track.LayerId == layer.Id);
            var volume = new Slider { Minimum = 0, Maximum = 1, Value = layer.Volume, Width = 260, Margin = new Thickness(12, 0, 0, 0) };
            var mute = new CheckBox { Content = "Mute", IsChecked = layer.IsMuted, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            volume.ValueChanged += (_, args) =>
            {
                layer.Volume = args.NewValue;
                ApplyLayerMix(layer.Id);
            };
            mute.Checked += (_, _) =>
            {
                layer.IsMuted = true;
                ApplyLayerMix(layer.Id);
            };
            mute.Unchecked += (_, _) =>
            {
                layer.IsMuted = false;
                ApplyLayerMix(layer.Id);
            };

            var heading = new TextBlock { Text = layer.Name.ToUpperInvariant(), FontSize = 16, FontWeight = FontWeights.SemiBold };
            var details = new TextBlock { Text = $"{sourceCount} source{(sourceCount == 1 ? string.Empty : "s")} · {activeCount} active", Margin = new Thickness(0, 4, 0, 0), Foreground = System.Windows.Media.Brushes.LightGray };
            var controls = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            controls.Children.Add(new TextBlock { Text = "Level", VerticalAlignment = VerticalAlignment.Center });
            controls.Children.Add(volume);
            controls.Children.Add(mute);

            var card = new Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 46, 54)), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10) };
            var content = new StackPanel();
            content.Children.Add(heading);
            content.Children.Add(details);
            content.Children.Add(controls);
            card.Child = content;
            MixerLayerPanel.Children.Add(card);
        }
    }

    private void UpdateProgress()
    {
        AdvanceFinishedMusic();
        var sessions = _audioEngine.GetSessions();
        var sessionsByTrackId = sessions.GroupBy(session => session.TrackId)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var row in _trackRows.Values)
        {
            if (sessionsByTrackId.TryGetValue(row.Track.Id, out var session))
            {
                row.SessionId = session.SessionId;
                row.Progress.Value = session.Length.TotalSeconds <= 0
                    ? 0
                    : Math.Clamp(session.Position.TotalSeconds / session.Length.TotalSeconds, 0, 1);
                row.Status.Text = session.IsPlaying ? "▶ Playing" : "⏸ Paused";
                row.PlayPause.Content = session.IsPlaying ? "⏸ Pause" : "▶ Play";
            }
            else
            {
                row.SessionId = null;
                row.Progress.Value = 0;
                row.Status.Text = "■ Stopped";
                row.PlayPause.Content = "▶ Play";
            }
        }

        UpdatePlaylistActiveIndicators(sessions);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _autosaveTimer.Stop();
        _settings.LastShutdownClean = true;
        _settingsStore.Save(_settings);
        _controlServer.Dispose();
        _audioEngine.Dispose();
        base.OnClosed(e);
    }

    private void AddTrackRow(Guid? sessionId, Track track)
    {
        if (_trackRows.TryGetValue(track.Id, out var existingRow))
        {
            existingRow.SessionId = sessionId;
            return;
        }

        var name = new TextBlock { Text = track.Name, FontWeight = FontWeights.SemiBold };
        var status = new TextBlock { Text = "▶ Playing", Margin = new Thickness(0, 3, 0, 0) };
        var progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
        var volume = new Slider { Minimum = 0, Maximum = 1, Value = track.Volume, Width = 130, Margin = new Thickness(6, 0, 0, 0) };
        var autoPlay = new CheckBox { Content = "Auto play", IsChecked = track.AutoPlayOnSceneActivation, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var layerSelector = new ComboBox
        {
            Width = 155,
            Margin = new Thickness(6, 0, 0, 0),
            DisplayMemberPath = nameof(LayerChoice.Display),
            SelectedValuePath = nameof(LayerChoice.LayerId)
        };
        var playlistSelector = new ComboBox
        {
            Width = 150,
            Margin = new Thickness(6, 0, 0, 0),
            DisplayMemberPath = nameof(PlaylistChoice.Display),
            SelectedValuePath = nameof(PlaylistChoice.PlaylistId),
            ToolTip = "Playlist pool"
        };
        var playPause = new Button { Content = "⏸ Pause", Padding = new Thickness(8, 3, 8, 3) };
        var stop = new Button { Content = "■ Stop", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
        var removeFromScene = new Button { Content = "Remove from Scene", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
        var deleteSource = new Button { Content = "Delete Source", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove this source from the entire Soundforge project" };
        var editSource = new Button { Content = "Edit", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Set this source's playback start and end times" };

        playPause.Click += (_, _) => RunPlaybackAction(() =>
        {
            var row = _trackRows[track.Id];
            var session = _audioEngine.GetSessions().FirstOrDefault(item => item.TrackId == track.Id);
            if (session is null)
            {
                row.SessionId = _audioEngine.Start(track, GetFadeInDuration(_selectedScene));
                ApplyLayerMix(track.LayerId);
                return;
            }

            if (session.IsPlaying)
            {
                _audioEngine.Pause(session.SessionId);
                playPause.Content = "▶ Play";
            }
            else
            {
                _audioEngine.Resume(session.SessionId);
                playPause.Content = "⏸ Pause";
            }
        });
        stop.Click += (_, _) => StopTrack(track.Id);
        removeFromScene.Click += (_, _) => RemoveTrackFromSelectedScene(track);
        deleteSource.Click += (_, _) => DeleteSourceFromProject(track);
        editSource.Click += (_, _) => EditSource(track);
        volume.ValueChanged += (_, args) =>
        {
            var activeSession = _audioEngine.GetSessions().FirstOrDefault(item => item.TrackId == track.Id);
            if (activeSession is not null)
                _audioEngine.SetTrackVolume(activeSession.SessionId, (float)args.NewValue);
            else
                track.Volume = args.NewValue;
        };
        autoPlay.Checked += (_, _) => track.AutoPlayOnSceneActivation = true;
        autoPlay.Unchecked += (_, _) => track.AutoPlayOnSceneActivation = false;
        layerSelector.SelectionChanged += (_, _) =>
        {
            if (_refreshingTrackSelectors)
                return;

            track.LayerId = layerSelector.SelectedValue is Guid layerId ? layerId : null;
            ApplyLayerPlaybackDefaults(track);
            AssignTrackToLayer(track, track.LayerId);
            ApplyLayerMix(track.LayerId);
            RefreshTrackLayerSelectors();
            RefreshTrackVisibility();
        };
        playlistSelector.SelectionChanged += (_, _) =>
        {
            if (_refreshingTrackSelectors || playlistSelector.SelectedValue is not Guid playlistId)
                return;

            AssignTrackToPlaylist(track, playlistId);
            ApplyLayerPlaybackDefaults(track);
            ApplyLayerMix(track.LayerId);
            RefreshTrackLayerSelectors();
            RefreshTrackVisibility();
            RenderLayers();
        };

        var controls = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(playPause);
        controls.Children.Add(stop);
        controls.Children.Add(removeFromScene);
        controls.Children.Add(deleteSource);
        controls.Children.Add(editSource);
        controls.Children.Add(new TextBlock { Text = "Volume", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        controls.Children.Add(volume);
        controls.Children.Add(autoPlay);
        controls.Children.Add(new TextBlock { Text = "Layer", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        controls.Children.Add(layerSelector);
        controls.Children.Add(new TextBlock { Text = "Pool", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        controls.Children.Add(playlistSelector);

        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(name);
        panel.Children.Add(status);
        panel.Children.Add(progress);
        panel.Children.Add(controls);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 0) });
        ActiveTracksPanel.Children.Add(panel);
        _trackRows[track.Id] = new TrackRow(panel, status, progress, track, layerSelector, playlistSelector, playPause) { SessionId = sessionId };
        RefreshTrackLayerSelectors();
        RefreshTrackVisibility();
    }

    private void StopTrack(Guid trackId)
    {
        var session = _audioEngine.GetSessions().FirstOrDefault(item => item.TrackId == trackId);
        if (session is not null)
            _audioEngine.Stop(session.SessionId, GetFadeOutDuration(_selectedScene));
    }

    private void EditSource(Track track)
    {
        TrackEditWindow editor;
        try
        {
            editor = new TrackEditWindow(track)
            {
                Owner = this
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Soundforge could not open this source for editing:\n\n{ex.Message}",
                "Edit Source",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (editor.ShowDialog() != true)
            return;

        var activeSession = _audioEngine.GetSessions().FirstOrDefault(item => item.TrackId == track.Id);
        if (activeSession is null)
            return;

        RunPlaybackAction(() =>
        {
            var restartedSessionId = _audioEngine.Transition([track], [activeSession.SessionId], TimeSpan.Zero, TimeSpan.Zero).Single();
            if (_trackRows.TryGetValue(track.Id, out var row))
                row.SessionId = restartedSessionId;
            ApplyLayerMix(track.LayerId);
        });
    }

    private void RemoveTrackFromSelectedScene(Track track)
    {
        if (_selectedScene is null)
            return;

        foreach (var playlist in _selectedScene.Layers.SelectMany(layer => layer.Playlists))
            playlist.Tracks.RemoveAll(existing => existing.Id == track.Id);

        if (_selectedScene.Layers.Any(layer => layer.Id == track.LayerId))
            track.LayerId = null;

        RefreshTrackLayerSelectors();
        RefreshTrackVisibility();
    }

    private void DeleteSourceFromProject(Track track)
    {
        var confirmation = MessageBox.Show(
            $"Delete '{track.Name}' from this Soundforge project and every scene?\n\nThe original audio file on disk will not be deleted.",
            "Delete Source",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var session = _audioEngine.GetSessions().FirstOrDefault(item => item.TrackId == track.Id);
        if (session is not null)
            _audioEngine.Stop(session.SessionId, TimeSpan.Zero);

        foreach (var playlist in _project.Scenes.SelectMany(scene => scene.Layers).SelectMany(layer => layer.Playlists))
            playlist.Tracks.RemoveAll(existing => existing.Id == track.Id);

        _trackRows.Remove(track.Id);
        RefreshTrackLayerSelectors();
        RefreshTrackVisibility();
    }

    private void RefreshTrackLayerSelectors()
    {
        _refreshingTrackSelectors = true;
        var layerChoices = GetLayerChoices();
        try
        {
            foreach (var trackRow in _trackRows.Values)
            {
                trackRow.LayerSelector.ItemsSource = layerChoices;
                trackRow.LayerSelector.SelectedValue = trackRow.Track.LayerId;
                trackRow.PlaylistSelector.ItemsSource = GetPlaylistChoices(trackRow.Track.LayerId);
                trackRow.PlaylistSelector.SelectedValue = GetTrackPlaylistId(trackRow.Track.Id);
            }
        }
        finally
        {
            _refreshingTrackSelectors = false;
        }
    }

    private IReadOnlyList<LayerChoice> GetLayerChoices()
    {
        var choices = new List<LayerChoice> { new(null, "Unassigned") };
        choices.AddRange(_project.Scenes.SelectMany(scene => scene.Layers.Select(layer => new LayerChoice(layer.Id, $"{scene.Name} / {layer.Name}"))));
        return choices;
    }

    private IReadOnlyList<PlaylistChoice> GetPlaylistChoices(Guid? layerId)
    {
        var layer = GetLayer(layerId);
        return layer?.Playlists.Select(playlist => new PlaylistChoice(playlist.Id, playlist.Name)).ToList() ?? [];
    }

    private Guid? GetTrackPlaylistId(Guid trackId) =>
        _project.Scenes.SelectMany(scene => scene.Layers)
            .SelectMany(layer => layer.Playlists)
            .FirstOrDefault(playlist => playlist.Tracks.Any(track => track.Id == trackId))?.Id;

    private void RefreshTrackVisibility()
    {
        if (SceneTracksHeader is null || EmptyTracksText is null)
            return;

        var isGlobalView = GlobalViewRadio.IsChecked == true;
        foreach (var expander in ActiveTracksPanel.Children.OfType<Expander>().ToList())
        {
            if (expander.Content is Panel section)
                section.Children.Clear();
            expander.Content = null;
        }
        ActiveTracksPanel.Children.Clear();
        _playlistHeadings.Clear();
        var visibleTrackCount = 0;
        var scenes = isGlobalView ? _project.Scenes : _selectedScene is null ? [] : [_selectedScene];
        var renderedTrackIds = new HashSet<Guid>();

        foreach (var scene in scenes)
        {
            foreach (var layer in scene.Layers)
            {
                foreach (var playlist in layer.Playlists)
                {
                    var rows = playlist.Tracks
                        .Where(track => renderedTrackIds.Add(track.Id))
                        .Select(track => _trackRows.TryGetValue(track.Id, out var row) ? row : null)
                        .Where(row => row is not null)
                        .Cast<TrackRow>()
                        .ToList();
                    if (rows.Count == 0)
                        continue;

                    var heading = isGlobalView
                        ? $"{scene.Name.ToUpperInvariant()}  /  {layer.Name.ToUpperInvariant()}  /  {playlist.Name.ToUpperInvariant()}"
                        : $"{layer.Name.ToUpperInvariant()}  /  {playlist.Name.ToUpperInvariant()}";
                    var headingControl = CreateLayerHeading(heading);
                    var expander = new Expander
                    {
                        Header = headingControl,
                        IsExpanded = playlist.IsExpanded,
                        Foreground = new SolidColorBrush(Color.FromRgb(242, 242, 242))
                    };
                    if (headingControl.Child is TextBlock headingText)
                    {
                        _playlistHeadings[playlist.Id] = new PlaylistHeadingState(
                            headingText,
                            heading,
                            layer,
                            playlist,
                            playlist.Tracks.Select(track => track.Id).ToHashSet());
                    }
                    if (playlist.IsExpanded)
                        expander.Content = CreateTrackSection(rows);
                    expander.Expanded += (_, _) =>
                    {
                        playlist.IsExpanded = true;
                        expander.Content ??= CreateTrackSection(rows);
                    };
                    expander.Collapsed += (_, _) =>
                    {
                        playlist.IsExpanded = false;
                        if (expander.Content is Panel collapsedSection)
                            collapsedSection.Children.Clear();
                        expander.Content = null;
                    };
                    ActiveTracksPanel.Children.Add(expander);
                    visibleTrackCount += rows.Count;
                }
            }
        }

        if (isGlobalView)
        {
            var unassignedRows = _trackRows.Values.Where(row => row.Track.LayerId is null).ToList();
            if (unassignedRows.Count > 0)
            {
                var unassignedExpander = new Expander
                {
                    Header = CreateLayerHeading("UNASSIGNED SOURCES"),
                    IsExpanded = false
                };
                unassignedExpander.Expanded += (_, _) =>
                {
                    unassignedExpander.Content ??= CreateTrackSection(unassignedRows);
                };
                unassignedExpander.Collapsed += (_, _) =>
                {
                    if (unassignedExpander.Content is Panel collapsedSection)
                        collapsedSection.Children.Clear();
                    unassignedExpander.Content = null;
                };
                ActiveTracksPanel.Children.Add(unassignedExpander);
                visibleTrackCount += unassignedRows.Count;
            }
        }

        SceneTracksHeader.Text = isGlobalView
            ? "GLOBAL TRACKS"
            : $"SCENE TRACKS: {_selectedScene?.Name.ToUpperInvariant() ?? "NONE"}";
        EmptyTracksText.Text = isGlobalView
            ? "Load one or more audio files to begin mixing."
            : "No tracks are assigned to this scene.";
        EmptyTracksText.Visibility = visibleTrackCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlaylistActiveIndicators(_audioEngine.GetSessions());
    }

    private void UpdatePlaylistActiveIndicators(IReadOnlyList<PlaybackSessionInfo> sessions)
    {
        foreach (var state in _playlistHeadings.Values)
        {
            var isActive = state.Layer.PlaybackBehavior switch
            {
                LayerPlaybackBehavior.Music => state.Layer.ActivePlaylistId == state.Playlist.Id,
                LayerPlaybackBehavior.Ambience => sessions.Any(session =>
                    session.IsPlaying && state.TrackIds.Contains(session.TrackId)),
                _ => false
            };

            state.Heading.Text = isActive
                ? $"{state.BaseHeading}  •  ACTIVE"
                : state.BaseHeading;
        }
    }

    private static StackPanel CreateTrackSection(IEnumerable<TrackRow> rows)
    {
        var section = new StackPanel();
        foreach (var row in rows)
        {
            row.Panel.Visibility = Visibility.Visible;
            section.Children.Add(row.Panel);
        }
        return section;
    }

    private static Border CreateLayerHeading(string heading) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(43, 51, 69)),
        CornerRadius = new CornerRadius(4),
        Margin = new Thickness(0, 12, 0, 2),
        Padding = new Thickness(10, 6, 10, 6),
        Child = new TextBlock
        {
            Text = heading,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(159, 208, 255))
        }
    };

    private IReadOnlyList<Playlist> GetAutoAmbiencePools(Scene scene)
    {
        var ambiencePools = scene.Layers
            .Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Ambience)
            .SelectMany(layer => layer.Playlists)
            .ToList();
        var activeMusicPoolIds = scene.Layers
            .Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Music && layer.ActivePlaylistId is not null)
            .Select(layer => layer.ActivePlaylistId!.Value)
            .ToHashSet();
        var hasConfiguredLinks = ambiencePools.Any(playlist => playlist.AutoActivateWithMusicPlaylistIds.Count > 0);
        return hasConfiguredLinks
            ? ambiencePools.Where(playlist => playlist.AutoActivateWithMusicPlaylistIds.Any(activeMusicPoolIds.Contains)).ToList()
            : ambiencePools;
    }

    private IEnumerable<Track> GetAutoAmbienceTracks(Scene scene) =>
        GetAutoAmbiencePools(scene)
            .SelectMany(playlist => playlist.Tracks)
            .Where(track => track.AutoPlayOnSceneActivation)
            .GroupBy(track => track.Id)
            .Select(group => group.First());

    private void SyncLinkedAmbience(Scene scene)
    {
        var allAmbienceTrackIds = scene.Layers
            .Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Ambience)
            .SelectMany(layer => layer.Playlists)
            .SelectMany(playlist => playlist.Tracks)
            .Select(track => track.Id)
            .ToHashSet();
        var targetTracks = GetAutoAmbienceTracks(scene).ToList();
        var activeSessions = _audioEngine.GetSessions();
        RunPlaybackAction(() => TransitionTracks(targetTracks,
            activeSessions.Where(session => allAmbienceTrackIds.Contains(session.TrackId)), scene));
    }

    private void ActivateScene(Scene scene, bool reportErrors = true) =>
        RunPlaybackAction(() => ActivateSceneCore(scene), reportErrors);

    private void ActivateSceneCore(Scene scene)
    {
        var musicSources = GetMusicTracks(scene).ToList();
        var selectedMusic = musicSources.Count == 0 ? null : musicSources[Random.Shared.Next(musicSources.Count)];
        var ambienceSources = GetAutoAmbienceTracks(scene);
        var targetTracks = ambienceSources
            .Append(selectedMusic)
            .Where(track => track is not null)
            .Cast<Track>()
            .GroupBy(track => track.Id)
            .Select(group => group.First())
            .ToList();
        TransitionTracks(targetTracks, _audioEngine.GetSessions(), scene);

        _activeScene = scene;
        CurrentSceneText.Text = $"ACTIVE SCENE: {scene.Name.ToUpperInvariant()}";
        RefreshSceneButtons();
        RefreshTrackVisibility();
    }

    private void TransitionTracks(IEnumerable<Track> targets, IEnumerable<PlaybackSessionInfo> affectedSessions, Scene scene)
    {
        var tracks = targets.DistinctBy(track => track.Id).ToList();
        var targetIds = tracks.Select(track => track.Id).ToHashSet();
        var activeIds = _audioEngine.GetSessions().Select(session => session.TrackId).ToHashSet();
        var newTracks = tracks.Where(track => !activeIds.Contains(track.Id)).ToList();
        var retiringIds = affectedSessions.Where(session => !targetIds.Contains(session.TrackId))
            .Select(session => session.SessionId).ToList();
        var startedIds = _audioEngine.Transition(newTracks, retiringIds, GetFadeInDuration(scene), GetFadeOutDuration(scene));
        for (var index = 0; index < newTracks.Count; index++)
        {
            AddTrackRow(startedIds[index], newTracks[index]);
            ApplyLayerMix(newTracks[index].LayerId);
            EmptyTracksText.Visibility = Visibility.Collapsed;
        }
    }

    private IEnumerable<Track> GetSceneTracks(Scene scene) =>
        scene.Layers.SelectMany(layer => layer.Playlists).SelectMany(playlist => playlist.Tracks);

    private IEnumerable<(Layer Layer, Track Track)> GetSceneSources(Scene scene) =>
        scene.Layers.SelectMany(layer => layer.Playlists.SelectMany(playlist => playlist.Tracks.Select(track => (layer, track))));

    private IEnumerable<Track> GetMusicTracks(Scene scene) =>
        scene.Layers
            .Where(layer => layer.PlaybackBehavior == LayerPlaybackBehavior.Music)
            .SelectMany(layer => layer.Playlists
                .Where(playlist => playlist.Id == layer.ActivePlaylistId)
                .SelectMany(playlist => playlist.Tracks))
            .GroupBy(track => track.Id)
            .Select(group => group.First());

    private Layer? GetLayer(Guid? layerId) =>
        layerId is Guid id
            ? _project.Scenes.SelectMany(scene => scene.Layers).FirstOrDefault(layer => layer.Id == id)
            : null;

    private void ApplyLayerMix(Guid? layerId)
    {
        var layer = GetLayer(layerId);
        if (layer is null)
            return;

        var gain = layer.IsMuted ? 0f : (float)layer.Volume;
        foreach (var session in _audioEngine.GetSessions())
        {
            if (_trackRows.TryGetValue(session.TrackId, out var row) && row.Track.LayerId == layer.Id)
                _audioEngine.SetSessionLayerGain(session.SessionId, gain);
        }
    }

    private void ApplyLayerPlaybackDefaults(Track track)
    {
        var behavior = GetLayer(track.LayerId)?.PlaybackBehavior;
        track.Loop = behavior == LayerPlaybackBehavior.Ambience;
        if (behavior == LayerPlaybackBehavior.Effects)
            track.AutoPlayOnSceneActivation = false;

        var session = _audioEngine.GetSessions().FirstOrDefault(item => item.TrackId == track.Id);
        if (session is not null)
            _audioEngine.SetLoop(session.SessionId, track.Loop);
    }

    private void AdvanceFinishedMusic()
    {
        if (_activeScene is null)
            return;

        var musicTracks = GetMusicTracks(_activeScene).ToList();
        if (musicTracks.Count == 0)
            return;

        var musicTrackIds = musicTracks.Select(track => track.Id).ToHashSet();
        var finishedMusicSessions = _audioEngine.GetSessions()
            .Where(session => musicTrackIds.Contains(session.TrackId)
                && session.HasReachedEnd)
            .ToList();

        foreach (var finishedSession in finishedMusicSessions)
        {
            _audioEngine.Stop(finishedSession.SessionId);
            var candidates = musicTracks.Where(track => track.Id != finishedSession.TrackId).ToList();
            var nextTrack = (candidates.Count > 0 ? candidates : musicTracks)[Random.Shared.Next(candidates.Count > 0 ? candidates.Count : musicTracks.Count)];
            RunPlaybackAction(() =>
            {
                var nextSessionId = _audioEngine.Start(nextTrack, GetFadeInDuration(_activeScene));
                AddTrackRow(nextSessionId, nextTrack);
                ApplyLayerMix(nextTrack.LayerId);
            });
        }
    }

    private void AssignTrackToLayer(Track track, Guid? layerId)
    {
        foreach (var existingPlaylist in _project.Scenes.SelectMany(scene => scene.Layers).SelectMany(layer => layer.Playlists))
            existingPlaylist.Tracks.RemoveAll(existing => existing.Id == track.Id);

        if (layerId is not Guid targetLayerId)
            return;

        var layer = _project.Scenes.SelectMany(scene => scene.Layers).FirstOrDefault(candidate => candidate.Id == targetLayerId);
        if (layer is null)
            return;

        EnsureLayerHasPlaylist(layer);
        var playlist = layer.Playlists.FirstOrDefault(candidate => candidate.Id == layer.ActivePlaylistId) ?? layer.Playlists[0];

        playlist.Tracks.Add(track);
    }

    private void AssignTrackToPlaylist(Track track, Guid playlistId)
    {
        var target = _project.Scenes.SelectMany(scene => scene.Layers)
            .SelectMany(layer => layer.Playlists.Select(playlist => (layer, playlist)))
            .FirstOrDefault(candidate => candidate.playlist.Id == playlistId);
        if (target.playlist is null)
            return;

        foreach (var existingPlaylist in _project.Scenes.SelectMany(scene => scene.Layers).SelectMany(layer => layer.Playlists))
            existingPlaylist.Tracks.RemoveAll(existing => existing.Id == track.Id);

        track.LayerId = target.layer.Id;
        target.playlist.Tracks.Add(track);
    }

    private static TimeSpan GetFadeInDuration(Scene? scene) =>
        scene is { UseCrossfade: true } ? TimeSpan.FromSeconds(scene.FadeInSeconds) : TimeSpan.Zero;

    private static TimeSpan GetFadeOutDuration(Scene? scene) =>
        scene is { UseCrossfade: true } ? TimeSpan.FromSeconds(scene.FadeOutSeconds) : TimeSpan.Zero;

    private sealed class TrackRow(StackPanel panel, TextBlock status, ProgressBar progress, Track track, ComboBox layerSelector, ComboBox playlistSelector, Button playPause)
    {
        public StackPanel Panel { get; } = panel;
        public TextBlock Status { get; } = status;
        public ProgressBar Progress { get; } = progress;
        public Track Track { get; } = track;
        public ComboBox LayerSelector { get; } = layerSelector;
        public ComboBox PlaylistSelector { get; } = playlistSelector;
        public Button PlayPause { get; } = playPause;
        public Guid? SessionId { get; set; }
    }

    private sealed record LayerChoice(Guid? LayerId, string Display);
    private sealed record PlaylistChoice(Guid PlaylistId, string Display);
    private sealed record PlaylistHeadingState(
        TextBlock Heading,
        string BaseHeading,
        Layer Layer,
        Playlist Playlist,
        HashSet<Guid> TrackIds);
}
