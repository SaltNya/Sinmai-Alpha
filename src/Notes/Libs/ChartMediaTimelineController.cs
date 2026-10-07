using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;
using SinmaiAlpha.ChartVisuals;

// Media event, seek and fade behavior ported from the supplied Alpha reference.
// Native adapters supply only music time, render frame and background leasing.
namespace SinmaiAlpha.Notes.Libs;

public class ChartMediaTimelineController : MonoBehaviour
{
    private readonly List<MediaChange> events = new();
    private readonly Dictionary<string, AudioClip> audioClips =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Sprite> imageSprites =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MediaChange, VideoPlayer> videoPlayers = new();

    private NativeMediaClock timeProvider;
    private AudioSource audioSource;
    private UnityWebRequest pendingAudioRequest;
    private SpriteRenderer imageRenderer;
    private SpriteRenderer outgoingImageRenderer;
    private Sprite videoSurfaceSprite;
    private string rootPath;
    private bool recording;
    private bool audioEnabled = true;
    private bool playbackActive;
    private bool prepared = true;
    private float? pendingPausedTimelineTime;
    private int prepareGeneration;
    private int cursor;
    private float lastTime = float.MinValue;
    private MediaChange activeAudio;
    private MediaChange activeOverlay;
    private VideoPlayer activeVideo;
    private VideoPlayer outgoingVideo;
    private VideoPlayer pendingSeekPlayer;
    private float pendingSeekStartedAt;
    private double pendingSeekTarget;
    private int pendingSeekGeneration;
    private int activeVideoGeneration;
    private float videoSyncAllowedAt;
    private float nextVideoSyncAt;
    private NativeMediaBackground backgroundManager;
    private int backgroundFitMode;
    private float overlayBlend;
    private float transitionStartBlend;
    private float transitionTargetBlend;
    private float transitionStartTime;
    private float transitionDuration;
    private bool stopOverlayAfterTransition;
    private bool replacementActive;
    private float replacementStartTime;
    private float replacementDuration;
    private float replacementProgress = 1f;
    private float outgoingStartAlpha;
    private readonly List<ChartMediaTimelineController> timelineControllers = new();
    private bool secondaryInstance;
    private int controllerTrack;
    private bool timelineVideoMode;
    private float parentVisibility = 1f;
    private string preparationError;

    public bool IsPrepared => prepared && timelineControllers.All(controller => controller.IsPrepared);
    public string PreparationError => !string.IsNullOrEmpty(preparationError)
        ? preparationError
        : timelineControllers.Select(controller => controller.PreparationError)
            .FirstOrDefault(error => !string.IsNullOrEmpty(error));

    public void Configure(
        List<MediaChange> mediaEvents,
        string chartRoot,
        NativeMediaClock provider,
        bool isRecording,
        NativeMediaBackground background)
    {
        backgroundManager = background;
        var allEvents = mediaEvents ?? new List<MediaChange>();
        if (!secondaryInstance)
        {
            timelineVideoMode = allEvents.Any(item => item.timelineClip && item.kind == "pvOverlay");
            EnsureTimelineControllers();
            for (var track = 0; track < timelineControllers.Count; track++)
            {
                var timelineTrack = track;
                timelineControllers[track].Configure(
                    allEvents.Where(item => item.timelineClip && item.kind == "pvOverlay" &&
                                            item.track == timelineTrack).ToList(),
                    chartRoot,
                    provider,
                    isRecording, background);
            }

            // Syntax PVOverlay events live on the root controller above both timeline lanes.
            mediaEvents = allEvents.Where(item => !(item.timelineClip && item.kind == "pvOverlay")).ToList();
        }

        prepareGeneration++;
        StopActiveMedia();
        ReleaseLoadedMedia();

        timeProvider = provider;
        rootPath = Path.GetFullPath(chartRoot ?? string.Empty);
        recording = isRecording;
        preparationError = null;
        events.Clear();
        if (mediaEvents != null)
            events.AddRange(mediaEvents
                .Select((item, sequence) => new { item, sequence })
                .Where(entry => entry.item != null)
                .OrderBy(entry => entry.item.time)
                .ThenBy(entry => entry.sequence)
                .Select(entry => entry.item));

        EnsureRuntimeObjects();
        cursor = 0;
        lastTime = float.MinValue;
        playbackActive = false;
        pendingPausedTimelineTime = null;
        prepared = events.Count == 0;
        if (prepared)
            return;

        StartCoroutine(PrepareMediaSafely(prepareGeneration));
    }

    public void SetPlaybackActive(bool active)
    {
        if (active)
            pendingPausedTimelineTime = null;
        playbackActive = active;
        if (!active)
        {
            StopActiveMedia();
            foreach (var controller in timelineControllers)
                controller.SetPlaybackActive(false);
            return;
        }
        ApplyAt(timeProvider != null ? timeProvider.AudioTime : 0f, true);
        foreach (var controller in timelineControllers)
            controller.SetPlaybackActive(true);
    }

    public void SetBackgroundFitMode(int fitMode)
    {
        backgroundFitMode = Mathf.Clamp(fitMode, 0, 1);
        
        backgroundManager?.SetBackgroundFitMode(backgroundFitMode);

        if (activeVideo != null)
            ApplyVideoScale(activeVideo);
        else if (activeOverlay != null && imageRenderer != null && imageRenderer.sprite != null)
            ApplyCoverScale(
                imageRenderer,
                imageRenderer.sprite.texture.width,
                imageRenderer.sprite.texture.height);
        foreach (var controller in timelineControllers)
            controller.SetBackgroundFitMode(fitMode);
    }

    public void SetAudioVolume(float volume)
    {
        EnsureRuntimeObjects();
        audioSource.volume = Mathf.Clamp01(volume);
        foreach (var controller in timelineControllers)
            controller.SetAudioVolume(volume);
    }

    public void CollectVisualTransforms(List<Transform> target)
    {
        if (target == null)
            return;
        AddVisualTransform(target, imageRenderer != null ? imageRenderer.transform : null);
        AddVisualTransform(target, outgoingImageRenderer != null ? outgoingImageRenderer.transform : null);
        foreach (var player in videoPlayers.Values)
            AddVisualTransform(target, player != null ? player.transform : null);
        foreach (var controller in timelineControllers)
            controller.CollectVisualTransforms(target);
    }

    private static void AddVisualTransform(List<Transform> target, Transform transform)
    {
        if (transform != null && !target.Contains(transform))
            target.Add(transform);
    }

    public void PausePlayback()
    {
        playbackActive = false;
        if (audioSource != null && audioSource.isPlaying)
            audioSource.Pause();
        if (activeVideo != null && activeVideo.isPlaying)
            activeVideo.Pause();
        if (outgoingVideo != null && outgoingVideo.isPlaying)
            outgoingVideo.Pause();
        foreach (var controller in timelineControllers)
            controller.PausePlayback();
    }

    public void SetPausedTimelineTime(float time)
    {
        pendingPausedTimelineTime = time;
        foreach (var controller in timelineControllers)
            controller.SetPausedTimelineTime(time);
        if (!prepared)
            return;
        pendingPausedTimelineTime = null;
        playbackActive = true;
        ApplyAt(time, true);
        PausePlayback();
    }

    public void ContinuePlayback()
    {
        playbackActive = true;
        ResumeAt(timeProvider != null ? timeProvider.AudioTime : 0f);
        foreach (var controller in timelineControllers)
            controller.ContinuePlayback();
    }

    private void ResumeAt(float time)
    {
        if (!prepared)
            return;
        if (time + 0.001f < lastTime)
        {
            RebuildAt(time);
            lastTime = time;
            return;
        }

        var hasActiveVisual = activeVideo != null ||
                              imageRenderer != null && imageRenderer.enabled && imageRenderer.sprite != null;
        if (!hasActiveVisual && events.Any(item => item.kind == "pvOverlay" && item.time <= time))
        {
            RebuildAt(time);
            lastTime = time;
            return;
        }

        if (activeVideo != null && activeOverlay != null && activeVideo.isPrepared)
        {
            var elapsed = Mathf.Max(0f, time - (float)activeOverlay.time);
            var sourceTime = activeOverlay.sourceOffset + elapsed;
            if (Math.Abs(activeVideo.time - sourceTime) > 0.08d)
                RequestVideoSeek(activeVideo, sourceTime, activeVideoGeneration);
            activeVideo.playbackSpeed = Time.captureFramerate != 0
                ? 1f
                : Mathf.Max(0.01f, timeProvider != null ? timeProvider.CurrentSpeed : 1f);
            var renderer = activeVideo.GetComponent<SpriteRenderer>();
            if (renderer != null)
                renderer.enabled = true;
            ApplyVideoScale(activeVideo);
            activeVideo.Play();
        }
        if (outgoingVideo != null && outgoingVideo.isPrepared)
            outgoingVideo.Play();
        if (activeAudio != null && audioSource != null && audioSource.clip != null)
            audioSource.UnPause();

        UpdateOverlayTransition(time);
        ApplyOverlayBlend();
        lastTime = time;
    }

    public void Tick()
    {
        if (!playbackActive || !prepared || timeProvider == null)
            return;
        if (!timeProvider.PlaybackStarted && !timeProvider.IsPreview)
            return;
        if (audioSource != null && activeAudio != null)
            audioSource.pitch = Mathf.Clamp(timeProvider.CurrentSpeed, 0.01f, 3f);
        foreach (var video in new[] { activeVideo, outgoingVideo })
            if (video != null && video.isPrepared)
                video.playbackSpeed = Mathf.Max(0.01f, timeProvider.CurrentSpeed);
        ApplyAt(timeProvider.AudioTime, false);
        foreach (var controller in timelineControllers) controller.Tick();
    }

    private IEnumerator PrepareMediaSafely(int generation)
    {
        var preparation = PrepareMedia(generation);
        try
        {
            while (generation == prepareGeneration)
            {
                bool more;
                try { more = preparation.MoveNext(); }
                catch (Exception error)
                {
                    SetPreparationError("Media preparation failed: " + error.Message);
                    prepared = true; // A media error must never prevent native notes from starting.
                    yield break;
                }
                if (!more) yield break;
                yield return preparation.Current;
            }
        }
        finally { (preparation as IDisposable)?.Dispose(); }
    }

    private IEnumerator PrepareMedia(int generation)
    {
        var enabledEvents = events.Where(item => item.enabled && !string.IsNullOrWhiteSpace(item.path)).ToList();
        foreach (var item in enabledEvents)
        {
            if (generation != prepareGeneration)
                yield break;
            var fullPath = ResolveMediaPath(item.path);
            if (fullPath == null || !File.Exists(fullPath))
            {
                SetPreparationError($"Missing media file: {item.path}");
                continue;
            }

            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (item.kind == "audio" && !recording && !audioClips.ContainsKey(item.path))
            {
                var audioType = extension switch
                {
                    ".ogg" => AudioType.OGGVORBIS,
                    ".wav" => AudioType.WAV,
                    ".mp3" => AudioType.MPEG,
                    _ => AudioType.UNKNOWN
                };
                using (var request = UnityWebRequestMultimedia.GetAudioClip(
                           new Uri(fullPath).AbsoluteUri, audioType))
                {
                    pendingAudioRequest = request;
                    request.timeout = 15;
                    yield return request.SendWebRequest();
                    pendingAudioRequest = null;
                    if (generation != prepareGeneration)
                        yield break;
                    if (!request.isNetworkError && !request.isHttpError)
                    {
                        var clip = DownloadHandlerAudioClip.GetContent(request);
                        if (clip != null) audioClips[item.path] = clip;
                        else SetPreparationError($"Failed to decode audio: {item.path}");
                    }
                    else
                        SetPreparationError($"Failed to load audio '{item.path}': {request.error}");
                }
            }
            else if (item.kind == "pvOverlay" && extension == ".mp4" &&
                      !videoPlayers.ContainsKey(item))
            {
                videoPlayers[item] = CreateVideoPlayer(item.path, fullPath);
            }
            else if (item.kind == "pvOverlay" && extension != ".mp4" &&
                     !imageSprites.ContainsKey(item.path))
            {
                var sprite = NativeMediaBackground.LoadSprite(fullPath);
                if (sprite != null && sprite.texture != null && sprite.texture.width > 0)
                    imageSprites[item.path] = sprite;
                else
                    SetPreparationError($"Failed to load image: {item.path}");
            }
        }

        foreach (var player in videoPlayers.Values)
            player.Prepare();
        var deadline = Time.realtimeSinceStartup + 15f;
        while (generation == prepareGeneration &&
               videoPlayers.Values.Any(player => player != null && !player.isPrepared) &&
               Time.realtimeSinceStartup < deadline)
            yield return null;
        if (generation != prepareGeneration)
            yield break;

        foreach (var pair in videoPlayers)
            if (pair.Value != null && !pair.Value.isPrepared)
                SetPreparationError($"Video preparation timed out: {pair.Key.path}");

        foreach (var player in videoPlayers.Values)
        {
            if (player == null || !player.isPrepared)
                continue;
            player.time = 0d;
            player.Play();
        }
        var decodeDeadline = Time.realtimeSinceStartup + 8f;
        while (generation == prepareGeneration && Time.realtimeSinceStartup < decodeDeadline &&
               videoPlayers.Values.Any(player => player != null && player.isPrepared &&
                   player.frame < 0 && (player.texture == null || player.texture.width <= 0)))
            yield return new WaitForEndOfFrame();
        foreach (var player in videoPlayers.Values)
        {
            if (player == null)
                continue;
            player.Pause();
            var renderer = player.GetComponent<SpriteRenderer>();
            if (renderer != null)
                renderer.enabled = false;
        }

        prepared = true;
        if (pendingPausedTimelineTime.HasValue)
        {
            var pausedTime = pendingPausedTimelineTime.Value;
            pendingPausedTimelineTime = null;
            playbackActive = true;
            ApplyAt(pausedTime, true);
            PausePlayback();
        }
        else if (playbackActive)
            ApplyAt(timeProvider != null ? timeProvider.AudioTime : 0f, true);
    }

    private void ApplyAt(float time, bool rebuild)
    {
        if (!prepared)
            return;
        if (!rebuild && timeProvider != null &&
            !timeProvider.PlaybackStarted && !timeProvider.IsPreview)
            return;
        if (rebuild || time < lastTime)
            RebuildAt(time);
        else
        {
            while (cursor < events.Count && events[cursor].time <= time)
                ApplyEvent(events[cursor++], time);
        }

        if (activeOverlay == null && FindOverlayAt(time) != null)
            RebuildAt(time);
        lastTime = time;

        if (activeAudio != null && audioSource != null && audioSource.clip != null)
        {
            var elapsed = Mathf.Max(0f, time - (float)activeAudio.time);
            if ((activeAudio.duration > 0d && elapsed >= activeAudio.duration) ||
                activeAudio.sourceOffset + elapsed >= audioSource.clip.length)
                StopAudio();
        }

        if (activeOverlay != null && activeVideo != null && activeVideo.isPrepared)
        {
            var elapsed = Mathf.Max(0f, time - (float)activeOverlay.time);
            if ((activeOverlay.duration > 0d && elapsed >= activeOverlay.duration) ||
                (activeVideo.length > 0d && activeOverlay.sourceOffset + elapsed >= activeVideo.length))
                StopOverlay();
            else if (!recording && Time.realtimeSinceStartup >= videoSyncAllowedAt &&
                     Time.realtimeSinceStartup >= nextVideoSyncAt)
            {
                nextVideoSyncAt = Time.realtimeSinceStartup + 1f;
                var expectedTime = activeOverlay.sourceOffset + elapsed;
                if (Math.Abs(activeVideo.time - expectedTime) > 0.35d)
                    RequestVideoSeek(activeVideo, expectedTime, activeVideoGeneration);
            }
            var renderer = activeVideo.GetComponent<SpriteRenderer>();
            if (renderer != null)
                renderer.enabled = true;
            if (playbackActive && !activeVideo.isPlaying)
                activeVideo.Play();
        }
        UpdateOverlayTransition(time);
    }

    private MediaChange FindOverlayAt(float time)
    {
        if (secondaryInstance)
        {
            return events
                .Where(item => item.timelineClip && item.enabled && item.kind == "pvOverlay" &&
                               item.time <= time &&
                               (item.duration <= 0d || time - item.time < item.duration))
                .OrderBy(item => item.time)
                .LastOrDefault();
        }

        MediaChange active = null;
        foreach (var item in events)
        {
            if (item.time > time)
                break;
            if (item.kind != "pvOverlay")
                continue;
            active = item.enabled ? item : null;
        }

        // A missing/timed-out overlay must not force a rebuild every frame,
        // which would repeatedly stop and restart otherwise valid AUDIO.
        if (active == null || !(imageSprites.TryGetValue(active.path, out var image) && image != null ||
            videoPlayers.TryGetValue(active, out var video) && video != null && video.isPrepared))
            return null;
        var elapsed = time - (float)active.time;
        return active.duration <= 0d || elapsed < active.duration ? active : null;
    }

    private void RebuildAt(float time)
    {
        StopActiveMedia();
        cursor = 0;
        if (secondaryInstance)
        {
            while (cursor < events.Count && events[cursor].time <= time)
                cursor++;
            var timelineOverlay = FindOverlayAt(time);
            if (timelineOverlay != null)
                StartOverlay(timelineOverlay, time);
            return;
        }

        MediaChange audio = null;
        MediaChange overlay = null;
        MediaChange previousOverlay = null;
        MediaChange overlayStop = null;
        while (cursor < events.Count && events[cursor].time <= time)
        {
            var item = events[cursor++];
            if (item.kind == "audio")
                audio = item.enabled ? item : null;
            else if (item.kind == "pvOverlay")
            {
                if (item.enabled)
                {
                    previousOverlay = overlayStop == null ? overlay : null;
                    overlay = item;
                    overlayStop = null;
                }
                else if (overlay != null)
                {
                    overlayStop = item;
                    previousOverlay = null;
                }
            }
        }
        if (audio != null)
            StartAudio(audio, time);
        if (overlay != null)
        {
            var replacementElapsed = Mathf.Max(0f, time - (float)overlay.time);
            if (previousOverlay != null && overlayStop == null &&
                replacementElapsed < overlay.transition)
                StartOverlay(previousOverlay, time);
            StartOverlay(overlay, time);
            if (overlayStop != null)
                BeginOverlayTransition(
                    0f,
                    overlayStop.transition,
                    time,
                    true,
                    Mathf.Max(0f, time - (float)overlayStop.time));
        }
    }

    private void ApplyEvent(MediaChange item, float now)
    {
        if (item.kind == "audio")
        {
            if (item.enabled)
                StartAudio(item, now);
            else
                StopAudio();
        }
        else if (item.kind == "pvOverlay")
        {
            if (item.enabled)
                StartOverlay(item, now);
            else if (secondaryInstance)
            {
                var timelineOverlay = FindOverlayAt(now);
                if (ReferenceEquals(timelineOverlay, activeOverlay))
                    return;
                if (timelineOverlay != null)
                    StartOverlay(timelineOverlay, now);
                else
                    StopOverlay();
            }
            else
                BeginOverlayTransition(0f, item.transition, now, true);
        }
    }

    private void StartAudio(MediaChange item, float now)
    {
        StopAudio();
        if (recording || !audioEnabled || audioSource == null || !audioClips.TryGetValue(item.path, out var clip) || clip == null)
            return;
        var elapsed = Mathf.Max(0f, now - (float)item.time);
        var sourceTime = (float)item.sourceOffset + elapsed;
        if ((item.duration > 0d && elapsed >= item.duration) || sourceTime >= clip.length)
            return;
        activeAudio = item;
        audioSource.clip = clip;
        audioSource.time = sourceTime;
        audioSource.pitch = Mathf.Clamp(timeProvider != null ? timeProvider.CurrentSpeed : 1f, 0.01f, 3f);
        audioSource.Play();
    }

    private void StopAudio()
    {
        activeAudio = null;
        if (audioSource == null)
            return;
        audioSource.Stop();
        audioSource.clip = null;
    }

    private void StartOverlay(MediaChange item, float now)
    {
        var elapsed = Mathf.Max(0f, now - (float)item.time);
        var sourceTime = item.sourceOffset + elapsed;
        var hasVideo = videoPlayers.TryGetValue(item, out var player) &&
                       player != null && player.isPrepared;
        var hasImage = imageSprites.TryGetValue(item.path, out var sprite) &&
                       imageRenderer != null;
        if (!hasVideo && !hasImage)
            return;
        if (item.duration > 0d && elapsed >= item.duration)
            return;
        if (hasVideo && player.length > 0d && sourceTime >= player.length)
            return;

        var replacing = activeOverlay != null && item.transition > 0f;
        if (replacing)
            CaptureOutgoingOverlay();
        else
            StopOverlayImmediate();

        if (hasVideo)
        {
            activeOverlay = item;
            activeVideo = player;
            activeVideoGeneration++;
            player.Pause();
            RequestVideoSeek(player, sourceTime, activeVideoGeneration);
            videoSyncAllowedAt = Time.realtimeSinceStartup + 0.75f;
            nextVideoSyncAt = videoSyncAllowedAt;
            player.playbackSpeed = Time.captureFramerate != 0
                ? 1f
                : Mathf.Max(0.01f, timeProvider != null ? timeProvider.CurrentSpeed : 1f);
            ApplyVideoScale(player);
            var renderer = player.GetComponent<SpriteRenderer>();
            ConfigureOverlayRenderer(renderer);
            if (replacing)
                renderer.sortingOrder++;
            renderer.enabled = true;
            player.Play();
            BeginOverlayAppearance(item, now, elapsed, replacing);
            return;
        }

        activeOverlay = item;
        imageRenderer.sprite = sprite;
        ConfigureOverlayRenderer(imageRenderer);
        if (replacing)
            imageRenderer.sortingOrder++;
        ApplyCoverScale(imageRenderer, sprite.texture.width, sprite.texture.height);
        imageRenderer.enabled = true;
        BeginOverlayAppearance(item, now, elapsed, replacing);
    }

    private void BeginOverlayAppearance(
        MediaChange item,
        float now,
        float elapsed,
        bool replacing)
    {
        if (replacing)
        {
            replacementActive = true;
            replacementDuration = Mathf.Max(0f, item.transition);
            replacementStartTime = now - elapsed;
            replacementProgress = replacementDuration <= 0f
                ? 1f
                : Mathf.Clamp01(elapsed / replacementDuration);
            BeginOverlayTransition(1f, item.transition, now, false, elapsed);
            if (replacementProgress >= 1f)
                ClearOutgoingOverlay();
            return;
        }

        replacementActive = false;
        replacementProgress = 1f;
        BeginOverlayTransition(1f, item.transition, now, false, elapsed);
    }

    private void CaptureOutgoingOverlay()
    {
        ClearOutgoingOverlay();
        outgoingStartAlpha = overlayBlend;
        if (activeVideo != null)
        {
            outgoingVideo = activeVideo;
            var renderer = outgoingVideo.GetComponent<SpriteRenderer>();
            if (renderer != null)
            {
                outgoingStartAlpha = renderer.color.a;
                ConfigureOverlayRenderer(renderer);
            }
            activeVideo = null;
        }
        else if (imageRenderer != null && imageRenderer.enabled &&
                 imageRenderer.sprite != null && outgoingImageRenderer != null)
        {
            outgoingImageRenderer.sprite = imageRenderer.sprite;
            outgoingImageRenderer.transform.position = imageRenderer.transform.position;
            outgoingImageRenderer.transform.rotation = imageRenderer.transform.rotation;
            outgoingImageRenderer.transform.localScale = imageRenderer.transform.localScale;
            ConfigureOverlayRenderer(outgoingImageRenderer);
            outgoingImageRenderer.color = imageRenderer.color;
            outgoingImageRenderer.enabled = true;
            outgoingStartAlpha = imageRenderer.color.a;
            imageRenderer.enabled = false;
            imageRenderer.sprite = null;
        }
        activeOverlay = null;
    }

    private void ClearOutgoingOverlay()
    {
        if (outgoingImageRenderer != null)
        {
            outgoingImageRenderer.enabled = false;
            outgoingImageRenderer.sprite = null;
        }
        if (outgoingVideo != null)
        {
            outgoingVideo.Pause();
            var renderer = outgoingVideo.GetComponent<SpriteRenderer>();
            if (renderer != null)
                renderer.enabled = false;
        }
        outgoingVideo = null;
        replacementActive = false;
        replacementProgress = 1f;
    }

    private void StopOverlay()
    {
        if (stopOverlayAfterTransition)
            return;
        BeginOverlayTransition(0f, activeOverlay?.transition ?? 0f,
            timeProvider != null ? timeProvider.AudioTime : 0f, true);
    }

    private void StopOverlayImmediate()
    {
        ClearOutgoingOverlay();
        activeOverlay = null;
        if (imageRenderer != null)
        {
            imageRenderer.enabled = false;
            imageRenderer.sprite = null;
        }
        if (activeVideo != null)
        {
            if (pendingSeekPlayer == activeVideo)
                pendingSeekPlayer = null;
            activeVideoGeneration++;
            activeVideo.Pause();
            var renderer = activeVideo.GetComponent<SpriteRenderer>();
            if (renderer != null)
                renderer.enabled = false;
        }
        activeVideo = null;
        stopOverlayAfterTransition = false;
        overlayBlend = 0f;
        ApplyOverlayBlend();
    }

    private void RequestVideoSeek(VideoPlayer player, double target, int generation)
    {
        if (player == null || !player.isPrepared)
            return;
        if (player.length > 0d)
            target = Math.Min(target, Math.Max(0d, player.length - 0.001d));
        target = Math.Max(0d, target);

        if (pendingSeekPlayer == player && pendingSeekGeneration == generation &&
            Math.Abs(pendingSeekTarget - target) < 0.05d &&
            Time.realtimeSinceStartup - pendingSeekStartedAt < 1.5f)
            return;

        pendingSeekPlayer = player;
        pendingSeekStartedAt = Time.realtimeSinceStartup;
        pendingSeekTarget = target;
        pendingSeekGeneration = generation;
        player.time = target;
    }

    private void HandleVideoSeekCompleted(VideoPlayer player)
    {
        if (pendingSeekPlayer != player || pendingSeekGeneration != activeVideoGeneration)
            return;
        if (Math.Abs(player.time - pendingSeekTarget) > 0.4d)
            return;

        pendingSeekPlayer = null;
        if (activeVideo != player)
            return;

        var renderer = player.GetComponent<SpriteRenderer>();
        if (renderer != null)
            renderer.enabled = true;
        ApplyVideoScale(player);
        if (playbackActive && !player.isPlaying)
            player.Play();
        videoSyncAllowedAt = Time.realtimeSinceStartup + 0.75f;
        nextVideoSyncAt = videoSyncAllowedAt;
    }

    private void BeginOverlayTransition(
        float target,
        float duration,
        float now,
        bool stopAfter,
        float elapsed = 0f)
    {
        transitionStartBlend = overlayBlend;
        transitionTargetBlend = Mathf.Clamp01(target);
        transitionDuration = Mathf.Max(0f, duration);
        transitionStartTime = now - Mathf.Max(0f, elapsed);
        stopOverlayAfterTransition = stopAfter;
        UpdateOverlayTransition(now);
    }

    private void UpdateOverlayTransition(float now)
    {
        if (replacementActive)
        {
            replacementProgress = replacementDuration <= 0f
                ? 1f
                : Mathf.Clamp01((now - replacementStartTime) / replacementDuration);
            if (replacementProgress >= 1f)
                ClearOutgoingOverlay();
        }
        var progress = transitionDuration <= 0f
            ? 1f
            : Mathf.Clamp01((now - transitionStartTime) / transitionDuration);
        overlayBlend = Mathf.Lerp(transitionStartBlend, transitionTargetBlend, progress);
        ApplyOverlayBlend();
        if (progress < 1f || !stopOverlayAfterTransition)
            return;

        StopOverlayImmediate();
    }

    private void ApplyOverlayBlend()
    {
        
        if (!secondaryInstance)
        {
            backgroundManager?.SetMediaOverlayBlend(timelineVideoMode ? 1f : overlayBlend);
            if (timelineVideoMode)
                foreach (var controller in timelineControllers)
                    controller.SetParentVisibility(1f - overlayBlend);
        }
        var activeAlpha = parentVisibility * overlayBlend *
                          (replacementActive ? replacementProgress : 1f);
        var color = new Color(1f, 1f, 1f, activeAlpha);
        if (imageRenderer != null && imageRenderer.enabled)
            imageRenderer.color = color;
        if (activeVideo != null)
        {
            var renderer = activeVideo.GetComponent<SpriteRenderer>();
            if (renderer != null && renderer.enabled)
                renderer.color = color;
        }
        if (replacementActive)
        {
            var outgoingColor = new Color(
                1f, 1f, 1f, parentVisibility * outgoingStartAlpha * (1f - replacementProgress));
            if (outgoingImageRenderer != null && outgoingImageRenderer.enabled)
                outgoingImageRenderer.color = outgoingColor;
            if (outgoingVideo != null)
            {
                var renderer = outgoingVideo.GetComponent<SpriteRenderer>();
                if (renderer != null && renderer.enabled)
                    renderer.color = outgoingColor;
            }
        }
    }

    private void SetParentVisibility(float value)
    {
        parentVisibility = Mathf.Clamp01(value);
        ApplyOverlayBlend();
    }

    private void StopActiveMedia()
    {
        StopAudio();
        StopOverlayImmediate();
    }

    private void EnsureRuntimeObjects()
    {
        
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = false;

        if (imageRenderer == null)
        {
            var imageObject = new GameObject("MediaOverlayImage");
            imageRenderer = imageObject.AddComponent<SpriteRenderer>();
            ConfigureOverlayRenderer(imageRenderer);
            imageRenderer.enabled = false;
        }
        if (outgoingImageRenderer == null)
        {
            var imageObject = new GameObject("MediaOverlayOutgoingImage");
            outgoingImageRenderer = imageObject.AddComponent<SpriteRenderer>();
            ConfigureOverlayRenderer(outgoingImageRenderer);
            outgoingImageRenderer.enabled = false;
        }
        if (videoSurfaceSprite == null)
            videoSurfaceSprite = Sprite.Create(
                new Texture2D(1, 1),
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                1f / 10.8f);
    }

    private void EnsureTimelineControllers()
    {
        while (timelineControllers.Count < 2)
        {
            var track = timelineControllers.Count;
            var trackObject = new GameObject("MediaTimelineTrack" + (track + 1));
            trackObject.transform.SetParent(transform, false);
            var controller = trackObject.AddComponent<ChartMediaTimelineController>();
            controller.secondaryInstance = true;
            controller.controllerTrack = track + 1;
            timelineControllers.Add(controller);
        }
    }

    private VideoPlayer CreateVideoPlayer(string relativePath, string fullPath)
    {
        var videoObject = new GameObject("MediaOverlayVideo_" + videoPlayers.Count);
        var renderer = videoObject.AddComponent<SpriteRenderer>();
        renderer.sprite = videoSurfaceSprite;
        ConfigureOverlayRenderer(renderer);
        renderer.enabled = false;

        var player = videoObject.AddComponent<VideoPlayer>();
        player.playOnAwake = false;
        player.isLooping = false;
        player.audioOutputMode = VideoAudioOutputMode.None;
        player.renderMode = VideoRenderMode.MaterialOverride;
        player.targetMaterialRenderer = renderer;
        player.targetMaterialProperty = "_MainTex";
        // Unity 2018 VideoPlayer uses the native game time source by default.
        player.skipOnDrop = !recording;
        player.url = new Uri(fullPath).AbsoluteUri;
        player.seekCompleted += HandleVideoSeekCompleted;
        player.errorReceived += (_, message) =>
            SetPreparationError($"Video '{relativePath}' failed: {message}");
        return player;
    }

    private void SetPreparationError(string message)
    {
        if (string.IsNullOrEmpty(preparationError))
            preparationError = message;
        Debug.LogWarning("[MediaTimeline] " + message);
    }

    private void ConfigureOverlayRenderer(SpriteRenderer renderer)
    {
        backgroundManager?.ConfigureRenderer(renderer, 3 - controllerTrack);
    }

    private void ApplyVideoScale(VideoPlayer player)
    {
        if (player == null || player.width == 0 || player.height == 0)
            return;
        ApplyCoverScale(player.GetComponent<SpriteRenderer>(), player.width, player.height);
    }

    private void ApplyCoverScale(SpriteRenderer renderer, float width, float height)
    {
        if (renderer == null || renderer.sprite == null || width <= 0f || height <= 0f)
            return;
        
        if (backgroundManager == null)
            return;
        var isSquareSurface = renderer.sprite == videoSurfaceSprite;
        backgroundManager.ApplyMediaScale(renderer, width, height, isSquareSurface);
    }

    private string ResolveMediaPath(string relativePath) => MediaCommands.ResolvePath(rootPath, relativePath);

    public void SetAudioEnabled(bool enabled)
    {
        if (audioEnabled == enabled) return;
        audioEnabled = enabled;
        StopAudio();
        if (enabled && prepared && timeProvider != null && timeProvider.PlaybackStarted)
        {
            MediaChange audio = null;
            foreach (var item in events.Where(e => e.kind == "audio" && e.time <= timeProvider.AudioTime))
                audio = item.enabled ? item : null;
            if (audio != null)
            {
                StartAudio(audio, timeProvider.AudioTime);
                if (!playbackActive) audioSource?.Pause();
            }
        }
        foreach (var controller in timelineControllers) controller.SetAudioEnabled(enabled);
    }

    public void Shutdown()
    {
        prepareGeneration++;
        if (pendingAudioRequest != null) { pendingAudioRequest.Abort(); pendingAudioRequest.Dispose(); pendingAudioRequest = null; }
        StopAllCoroutines();
        playbackActive = false;
        StopActiveMedia();
        ReleaseLoadedMedia();
        foreach (var controller in timelineControllers) controller.Shutdown();
        prepared = true;
        if (!secondaryInstance) backgroundManager?.Restore();
    }

    private void ReleaseLoadedMedia()
    {
        foreach (var clip in audioClips.Values)
            if (clip != null)
                Destroy(clip);
        audioClips.Clear();

        foreach (var sprite in imageSprites.Values)
        {
            if (sprite == null)
                continue;
            if (sprite.texture != null)
                Destroy(sprite.texture);
            Destroy(sprite);
        }
        imageSprites.Clear();

        foreach (var player in videoPlayers.Values)
            if (player != null)
            {
                player.seekCompleted -= HandleVideoSeekCompleted;
                Destroy(player.gameObject);
            }
        videoPlayers.Clear();
        pendingSeekPlayer = null;
    }

    private void OnDestroy()
    {
        Shutdown();
        if (imageRenderer != null)
            Destroy(imageRenderer.gameObject);
        if (outgoingImageRenderer != null)
            Destroy(outgoingImageRenderer.gameObject);
        if (videoSurfaceSprite != null)
        {
            if (videoSurfaceSprite.texture != null)
                Destroy(videoSurfaceSprite.texture);
            Destroy(videoSurfaceSprite);
        }
    }
}
