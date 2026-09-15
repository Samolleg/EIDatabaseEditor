using System.IO;
using System.Windows;
using System.Windows.Media;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class ResFileManagerDialog
{
    private MediaPlayer? _resAudio;
    private CancellationTokenSource? _audioCancellation;
    private string? _audioPath;
    private ResManagerFileRow? _audioRow;
    private ResFileManagerArchive? _audioArchive;
    private bool _audioPaused;

    private void UpdateAudioSelection(ResManagerFileRow? row)
    {
        if (ReferenceEquals(row, _audioRow) && ReferenceEquals(_archive, _audioArchive)) return;
        StopResAudio();
        _audioRow = row;
        _audioArchive = _archive;
        AudioPanel.Visibility = row is not null && row.State != "Будет удалён" &&
            row.FileType is "WAV" or "MP3" ? Visibility.Visible : Visibility.Collapsed;
        AudioStatus.Text = "Прослушать выбранный файл";
    }

    private void StopResAudio()
    {
        _audioCancellation?.Cancel();
        _audioCancellation?.Dispose();
        _audioCancellation = null;
        var player = _resAudio;
        _resAudio = null;
        player?.Close();
        DeleteAudioTemp(_audioPath);
        _audioPath = null;
        _audioPaused = false;
        AudioPlay.IsEnabled = true;
        AudioPause.IsEnabled = false;
    }

    private static void DeleteAudioTemp(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async void AudioPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_audioPaused && _resAudio is not null)
        {
            _resAudio.Play();
            _audioPaused = false;
            AudioPause.IsEnabled = true;
            AudioStatus.Text = "Воспроизведение";
            return;
        }
        var row = _audioRow;
        var archive = _audioArchive;
        if (row is null || archive is null) return;
        StopResAudio();
        var cancellation = _audioCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        string target = Path.Combine(Path.GetTempPath(), $"ei-res-audio-{Guid.NewGuid():N}.{row.FileType.ToLowerInvariant()}");
        AudioPlay.IsEnabled = false;
        AudioStatus.Text = "Загрузка звука…";
        try
        {
            _edits.TryGetValue(row.ResourceName, out var edit);
            if (edit?.ImportedFile is { } imported)
                await ResFileManagerArchive.ExtractImportedAsync(imported, target, cancellationToken: token);
            else
                await archive.ExtractAsync(row.ResourceName, target, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            _audioPath = target;
            var player = _resAudio = new MediaPlayer();
            player.MediaOpened += (_, _) =>
            {
                if (!ReferenceEquals(_resAudio, player)) return;
                AudioPlay.IsEnabled = true;
                AudioPause.IsEnabled = true;
                AudioStatus.Text = "Воспроизведение";
            };
            player.MediaEnded += (_, _) =>
            {
                if (!ReferenceEquals(_resAudio, player)) return;
                StopResAudio();
                AudioStatus.Text = "Воспроизведение завершено";
            };
            player.MediaFailed += (_, args) =>
            {
                if (!ReferenceEquals(_resAudio, player)) return;
                StopResAudio();
                AudioStatus.Text = "Не удалось воспроизвести: " + args.ErrorException.Message;
            };
            player.Open(new Uri(target));
            player.Play();
        }
        catch (OperationCanceledException) { DeleteAudioTemp(target); }
        catch (Exception ex)
        {
            DeleteAudioTemp(target);
            if (token.IsCancellationRequested) return;
            StopResAudio();
            AudioStatus.Text = "Не удалось открыть звук: " + ex.Message;
        }
    }

    private void AudioPause_Click(object sender, RoutedEventArgs e)
    {
        _resAudio?.Pause();
        _audioPaused = true;
        AudioPause.IsEnabled = false;
        AudioStatus.Text = "Пауза";
    }

    private void AudioStop_Click(object sender, RoutedEventArgs e)
    {
        StopResAudio();
        AudioStatus.Text = "Остановлено";
    }
}
