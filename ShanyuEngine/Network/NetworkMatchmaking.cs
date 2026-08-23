using System; using System.Collections.Generic; using System.Net; using System.Net.Sockets; using System.Threading; using System.Threading.Tasks; namespace ShanyuEngine.Network;

public enum MatchmakingRegion { Auto, USEast, USWest, Europe, Asia, China, SouthAmerica, Oceania }
public enum MatchmakingMode { QuickPlay, Ranked, Casual, Custom, Training, CoOp, BattleRoyale }
public enum GameMode { TeamDeathmatch, CaptureTheFlag, Domination, Payload, FreeForAll, Arena, BattleRoyale, Custom }
public enum SkillBucket { Bronze = 0, Silver = 1000, Gold = 2000, Platinum = 3000, Diamond = 4000, Master = 5000, Grandmaster = 6000 }

public struct PlayerMatchData
{
    public string PlayerId, DisplayName; public int Rank, Level, SkillRating;
    public GameMode PreferredMode; public Dictionary<string, int> Stats = new();
    public float Latency; public string Region = "auto";
    public int TeamIndex; public bool IsPartyLeader, IsReady;
    public PlayerMatchData() { Region = "auto"; }
}

public struct MatchConfig
{
    public GameMode Mode = GameMode.TeamDeathmatch;
    public int MaxPlayers = 10, MinPlayers = 2;
    public int TeamSize = 0; public bool HasBots, AllowLateJoin;
    public SkillBucket MinSkill, MaxSkill;
    public int TimeLimit = 600, ScoreLimit = 100;
    public string MapPool = "default"; public string[] EnabledMaps;
    public MatchConfig() { EnabledMaps = Array.Empty<string>(); }
}

public struct MatchResult
{
    public string MatchId, ServerAddress; public int ServerPort;
    public string ConnectionToken; public PlayerMatchData[] Players;
    public MatchConfig Config; public int EstimatedWaitTime;
    public DateTime CreatedAt;
}

public struct LeaderboardEntry
{
    public int Rank; public string PlayerId, DisplayName;
    public int Rating; public int Wins, Losses, Draws;
    public float WinRate; public string Title;
    public Dictionary<string, int> Stats = new();
    public LeaderboardEntry() { Stats = new(); }
}

public sealed class MatchmakingQueue
{
    private readonly List<PlayerMatchData> _queue = new();
    private readonly object _lock = new();
    private int _minPlayers = 2, _maxPlayers = 10;
    private float _maxWaitTime = 120;
    private bool _active;

    public int QueueSize { get { lock (_lock) return _queue.Count; } }

    public void Start() { _active = true; }
    public void Stop() { _active = false; lock (_lock) _queue.Clear(); }

    public void Enqueue(PlayerMatchData player)
    {
        lock (_lock) _queue.Add(player);
    }

    public bool Dequeue(string playerId)
    {
        lock (_lock) return _queue.RemoveAll(p => p.PlayerId == playerId) > 0;
    }

    public List<PlayerMatchData[]> FindMatches()
    {
        var matches = new List<PlayerMatchData[]>();
        if (!_active || _queue.Count < _minPlayers) return matches;

        lock (_lock)
        {
            _queue.Sort((a, b) => a.SkillRating.CompareTo(b.SkillRating));

            while (_queue.Count >= _minPlayers)
            {
                var match = new List<PlayerMatchData>();
                for (int i = 0; i < Math.Min(_maxPlayers, _queue.Count); i++)
                    match.Add(_queue[i]);

                if (match.Count >= _minPlayers)
                {
                    matches.Add(match.ToArray());
                    _queue.RemoveRange(0, match.Count);
                }
                else break;
            }
        }
        return matches;
    }

    public int GetEstimatedWaitTime(int skillRating)
    {
        int similarPlayers;
        lock (_lock) similarPlayers = _queue.FindAll(p => Math.Abs(p.SkillRating - skillRating) < 500).Count;
        if (similarPlayers >= _minPlayers) return 5;
        if (similarPlayers >= 2) return 15 + (int)((float)_maxWaitTime * (1f - (float)similarPlayers / _minPlayers));
        return (int)_maxWaitTime;
    }
}

public sealed class SkillRatingSystem
{
    private readonly Dictionary<string, int> _ratings = new();
    private const int DefaultRating = 1500;
    private const int KFactor = 32;

    public int GetRating(string playerId) => _ratings.TryGetValue(playerId, out var r) ? r : DefaultRating;

    public void SetRating(string playerId, int rating) => _ratings[playerId] = rating;

    public (int newRatingA, int newRatingB) CalculateResult(int ratingA, int ratingB, float scoreA, float scoreB)
    {
        float expectedA = 1f / (1f + MathF.Pow(10, (ratingB - ratingA) / 400f));
        float expectedB = 1f - expectedA;
        float actualA = scoreA > scoreB ? 1 : scoreA == scoreB ? 0.5f : 0;
        float actualB = 1f - actualA;
        int newA = ratingA + (int)(KFactor * (actualA - expectedA));
        int newB = ratingB + (int)(KFactor * (actualB - expectedB));
        return (newA, newB);
    }

    public void ApplyMatchResult(string playerA, string playerB, int scoreA, int scoreB)
    {
        int rA = GetRating(playerA), rB = GetRating(playerB);
        var (nA, nB) = CalculateResult(rA, rB, scoreA, scoreB);
        _ratings[playerA] = Math.Max(0, nA);
        _ratings[playerB] = Math.Max(0, nB);
    }

    public SkillBucket GetBucket(int rating) => rating switch
    {
        < 1000 => SkillBucket.Bronze, < 2000 => SkillBucket.Silver, < 3000 => SkillBucket.Gold,
        < 4000 => SkillBucket.Platinum, < 5000 => SkillBucket.Diamond, < 6000 => SkillBucket.Master,
        _ => SkillBucket.Grandmaster
    };

    public static string BucketName(SkillBucket b) => b switch
    {
        SkillBucket.Bronze => "Bronze", SkillBucket.Silver => "Silver",
        SkillBucket.Gold => "Gold", SkillBucket.Platinum => "Platinum",
        SkillBucket.Diamond => "Diamond", SkillBucket.Master => "Master",
        SkillBucket.Grandmaster => "Grandmaster", _ => "Unranked"
    };
}

public sealed class Leaderboard
{
    private readonly List<LeaderboardEntry> _entries = new();
    private readonly object _lock = new();
    public int MaxEntries = 1000;

    public void SubmitScore(string playerId, string displayName, int rating, Dictionary<string, int>? stats = null)
    {
        lock (_lock)
        {
            var existing = _entries.FindIndex(e => e.PlayerId == playerId);
            if (existing >= 0)
            {
                var e = _entries[existing];
                if (rating > e.Rating) { e.Rating = rating; if (stats != null) e.Stats = stats; _entries[existing] = e; }
            }
            else
            {
                _entries.Add(new LeaderboardEntry
                {
                    Rank = 0, PlayerId = playerId, DisplayName = displayName,
                    Rating = rating, Wins = 0, Losses = 0, Stats = stats ?? new Dictionary<string, int>()
                });
            }
            SortAndRank();
        }
    }

    public void RecordMatch(string playerId, bool won)
    {
        lock (_lock)
        {
            int idx = _entries.FindIndex(e => e.PlayerId == playerId);
            if (idx < 0) return;
            var e = _entries[idx];
            if (won) e.Wins++; else e.Losses++;
            e.WinRate = e.Wins + e.Losses > 0 ? (float)e.Wins / (e.Wins + e.Losses) : 0;
            _entries[idx] = e;
        }
    }

    public LeaderboardEntry[] GetTop(int count = 100)
    {
        lock (_lock) return _entries.GetRange(0, Math.Min(count, _entries.Count)).ToArray();
    }

    public LeaderboardEntry[] GetAroundPlayer(string playerId, int range = 10)
    {
        lock (_lock)
        {
            int idx = _entries.FindIndex(e => e.PlayerId == playerId);
            if (idx < 0) return Array.Empty<LeaderboardEntry>();
            int start = Math.Max(0, idx - range / 2);
            int end = Math.Min(_entries.Count, start + range);
            return _entries.GetRange(start, end - start).ToArray();
        }
    }

    private void SortAndRank()
    {
        _entries.Sort((a, b) => b.Rating.CompareTo(a.Rating));
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i]; e.Rank = i + 1; _entries[i] = e;
        }
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
    }
}
