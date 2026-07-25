using ClassInterpreter.Core.Audio;

namespace ClassInterpreter.Core.Speech;

public sealed record VoiceFingerprint(double Level, double CrossingRate, double SpectralChange)
{
    public static VoiceFingerprint FromPcm(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 4) return new(0, 0, 0);
        var samples = pcm.Length / 2;
        double level = 0, change = 0;
        var crossings = 0;
        short previous = BitConverter.ToInt16(pcm[..2]);
        for (var index = 0; index < samples; index++)
        {
            var current = BitConverter.ToInt16(pcm.Slice(index * 2, 2));
            level += Math.Abs((double)current) / short.MaxValue;
            if ((current >= 0) != (previous >= 0)) crossings++;
            change += Math.Abs((double)current - previous) / ushort.MaxValue;
            previous = current;
        }
        return new(level / samples, (double)crossings / samples, change / samples);
    }

    public static VoiceFingerprint Average(IEnumerable<VoiceFingerprint> values)
    {
        var items = values.ToArray();
        return items.Length == 0 ? new(0, 0, 0) : new(items.Average(x => x.Level), items.Average(x => x.CrossingRate), items.Average(x => x.SpectralChange));
    }
}

public sealed class VoiceLanguageProfile
{
    private readonly Dictionary<string, RunningVoiceProfile> _profiles = new(StringComparer.Ordinal);

    public void Observe(TranslationDirection route, VoiceFingerprint fingerprint)
    {
        if (fingerprint.Level < 0.002) return;
        if (!_profiles.TryGetValue(route.Id, out var profile)) _profiles[route.Id] = profile = new();
        profile.Add(fingerprint);
    }

    public TranslationDirection? Resolve(TranslationDirection selected, VoiceFingerprint fingerprint)
    {
        var candidates = selected == TranslationDirection.JapaneseChineseBidirectional
            ? new[] { TranslationDirection.ChineseToJapanese, TranslationDirection.JapaneseToChinese }
            : new[] { TranslationDirection.ChineseToEnglish, TranslationDirection.EnglishToChinese };
        if (fingerprint.Level < 0.002 || candidates.Any(item => !_profiles.TryGetValue(item.Id, out var p) || p.Count < 3)) return null;
        var distances = candidates.Select(item => (Route: item, Distance: _profiles[item.Id].Distance(fingerprint))).OrderBy(item => item.Distance).ToArray();
        // Voice colour is only supporting evidence. Require a clearly closer profile.
        return distances[0].Distance < 0.75 * Math.Max(0.0001, distances[1].Distance) ? distances[0].Route : null;
    }

    public bool HasTrainedPair(TranslationDirection selected)
    {
        var candidates = selected == TranslationDirection.JapaneseChineseBidirectional
            ? new[] { TranslationDirection.ChineseToJapanese, TranslationDirection.JapaneseToChinese }
            : new[] { TranslationDirection.ChineseToEnglish, TranslationDirection.EnglishToChinese };
        return candidates.All(item => _profiles.TryGetValue(item.Id, out var profile) && profile.Count >= 3);
    }

    public TranslationDirection? ResolveSelf(TranslationDirection selected, VoiceFingerprint fingerprint)
    {
        var selfRoute = selected == TranslationDirection.JapaneseChineseBidirectional
            ? TranslationDirection.ChineseToJapanese
            : TranslationDirection.ChineseToEnglish;
        if (fingerprint.Level < 0.002 || !_profiles.TryGetValue(selfRoute.Id, out var profile) || profile.Count < 3)
            return null;
        return profile.Distance(fingerprint) < 0.70 ? selfRoute : null;
    }

    public IReadOnlyDictionary<string, VoiceProfileSnapshot> Snapshot() => _profiles.ToDictionary(
        item => item.Key,
        item => new VoiceProfileSnapshot(item.Value.Count, item.Value.Mean.Level, item.Value.Mean.CrossingRate, item.Value.Mean.SpectralChange),
        StringComparer.Ordinal);

    public void Restore(IReadOnlyDictionary<string, VoiceProfileSnapshot>? values)
    {
        _profiles.Clear();
        if (values is null) return;
        foreach (var item in values.Where(item => item.Value.Count > 0))
            _profiles[item.Key] = RunningVoiceProfile.From(item.Value);
    }

    private sealed class RunningVoiceProfile
    {
        public int Count { get; private set; }
        public VoiceFingerprint Mean { get; private set; } = new(0, 0, 0);
        public void Add(VoiceFingerprint value)
        {
            var next = Math.Min(40, Count + 1);
            var weight = Count == 0 ? 1 : 1d / next;
            Mean = new(Mean.Level + (value.Level - Mean.Level) * weight, Mean.CrossingRate + (value.CrossingRate - Mean.CrossingRate) * weight, Mean.SpectralChange + (value.SpectralChange - Mean.SpectralChange) * weight);
            Count = next;
        }
        public double Distance(VoiceFingerprint value)
        {
            // Level is affected by distance, so timbre-like crossing/change features carry more weight.
            var level = (value.Level - Mean.Level) / 0.08;
            var crossing = (value.CrossingRate - Mean.CrossingRate) / 0.12;
            var change = (value.SpectralChange - Mean.SpectralChange) / 0.12;
            return Math.Sqrt(level * level * 0.15 + crossing * crossing * 0.45 + change * change * 0.40);
        }
        public static RunningVoiceProfile From(VoiceProfileSnapshot value) => new() { Count = Math.Min(40, value.Count), Mean = new(value.Level, value.CrossingRate, value.SpectralChange) };
    }
}

public sealed record VoiceProfileSnapshot(int Count, double Level, double CrossingRate, double SpectralChange);
