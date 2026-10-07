using System.Collections.Generic;
using SinmaiAlpha.ChartVisuals;
using Manager;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // All index-based tables below belong to one reader. Their existing helpers
    // run under ChartRuntimeContext, including nested native callbacks.
    private sealed class ChartRuntimeState
    {
        public ChartRuntimeState() { }
        public bool Enabled;
        public bool Classified;
        public readonly Dictionary<NoteData, float> NoteSpeedMultipliers = new Dictionary<NoteData, float>();
        public readonly Dictionary<int, float> NoteSpeedByIndex = new Dictionary<int, float>();
        public readonly Queue<float> PendingNoteSpeedMultipliers = new Queue<float>();
        public readonly Queue<float> PendingTouchSpeedMultipliers = new Queue<float>();
        public NoteData _currentNoteData;
        public int _noteCountBeforeLoad;
        public float? _optionalSpeedForCurrentLoad;
        public string _removedOptionalSpeedString;
        public bool _optionalSpeedRemoved;
        public string _optionalStreamIdForCurrentLoad;
        public string _removedOptionalStreamString;
        public bool _optionalStreamRemoved;
        public readonly Dictionary<int, CustomNoteKind> NoteKinds = new Dictionary<int, CustomNoteKind>();
        public List<Manager.NoteData> _activeNoteList;
        public bool _pendingMineForCurrentLoad;
        public bool _pendingTouchBreakForCurrentLoad;
        public bool _pendingTouchStarForCurrentLoad;
        public readonly Queue<bool> PendingMineFlags = new Queue<bool>();
        public readonly Queue<bool> PendingTouchBreakFlags = new Queue<bool>();
        public readonly Queue<bool> PendingTouchStarFlags = new Queue<bool>();
        public readonly List<(int Bar, int Grid, string Text)> PendingSvSegments = new List<(int, int, string)>();
        public readonly List<(int Bar, int Grid, string Text)> PendingHsSegments = new List<(int, int, string)>();
        public readonly Dictionary<string, List<(float Msec, float Mult)>> SvCurves = new Dictionary<string, List<(float Msec, float Mult)>>();
        public readonly Dictionary<string, List<(float Msec, float Mult)>> HsCurves = new Dictionary<string, List<(float Msec, float Mult)>>();
        public readonly Dictionary<string, List<float>> SvClearTimes = new Dictionary<string, List<float>>();
        public readonly Dictionary<string, List<float>> HsClearTimes = new();
        public readonly Dictionary<string, List<(float Msec, float Mult, float Cum)>> SvCumCurves = new Dictionary<string, List<(float Msec, float Mult, float Cum)>>();
        public readonly Dictionary<int, float> SvScrollPosByNoteIndex = new Dictionary<int, float>();
        public readonly Dictionary<int, float> SvWindowByNoteIndex = new Dictionary<int, float>();
        public readonly Dictionary<int, string> SvTypeByNoteIndex = new Dictionary<int, string>();
        public readonly Dictionary<int, string> StreamTypeByNoteIndex = new Dictionary<int, string>();
        public readonly Dictionary<int, float> SpeedMultByNoteIndex = new Dictionary<int, float>();
        public readonly Dictionary<int, float> HsMultByNoteIndex = new Dictionary<int, float>();
        public readonly HashSet<int> ReferenceMotionByNoteIndex = new();
        public readonly HashSet<int> IgnoreSvByNoteIndex = new();
        public readonly Dictionary<int, float> SvTailScrollPosByNoteIndex = new Dictionary<int, float>();
        public readonly Dictionary<int, List<(float Msec, float Scroll, float Max)>> NoteScrollTableByNoteIndex = new Dictionary<int, List<(float Msec, float Scroll, float Max)>>();
        public readonly Dictionary<int, List<int>> EachChildByNoteIndex = new Dictionary<int, List<int>>();
        public bool _loggedSvInject;
        public bool _loggedSvLeadScale;
        public readonly List<(int Bar, int Grid, string Text)> PendingBounceSegments = new List<(int, int, string)>();
        public readonly Dictionary<string, List<(float Msec, float DurationSec)>> BounceSegmentsByType = new Dictionary<string, List<(float, float)>>();
        public readonly Dictionary<int, float> BounceDurationByNoteIndex = new Dictionary<int, float>();
        public readonly Dictionary<int, string> BounceTypeByNoteIndex = new Dictionary<int, string>();
        public bool _loggedBounce;
        public readonly Dictionary<int, float> SpawnRadiusByNoteIndex = new();
        public readonly List<(float Msec, float Mult, float Cum)> MajdataSlideSv = new List<(float, float, float)>();
        public readonly List<(int Bar, int Grid, string Kind, string Text)> PendingRingSegments = new();
        public readonly List<RingChange> RingChanges = new();
        public readonly Dictionary<int, RingValue> RingValuesByNoteIndex = new();
        public readonly List<(int Bar, int Grid, string Kind, string Text)> PendingVisualCommands = new();
        public string pendingNoteSkin;
        public string pendingBorrowed;
        public string pendingTouchRadius;
        public string pendingStarHead;
        public bool pendingFirework;
        public string pendingVisualMarker;
        public bool pendingFakeMarker;
        public bool removedFakeMarker;
        public int pendingDZoneKey = -1;
        public bool removedDZoneMarker;
        public readonly List<(int Bar, int Grid, string Kind, string Text)> PendingPresentation = new();
        public readonly List<(int Bar, int Grid, string Kind, string Text)> PendingMedia = new();
        public readonly List<(int Bar, int Grid, string Text)> PendingSubtitles = new();
        public readonly List<(int Bar, int Grid, string Text)> PendingNoiseZones = new();
    }
}
