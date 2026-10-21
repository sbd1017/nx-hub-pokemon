using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NxHub.Pokemon.Requests;

public enum RequestKind
{
    /// <summary>The bot trades the requested Pokémon to the user.</summary>
    Trade,

    /// <summary>The bot opens a link trade, reads the partner's trainer details from what they show, and backs out without trading.</summary>
    TrainerInfo,
}

public enum RequestStatus
{
    Queued,
    Starting,
    Searching,
    Finished,
    Canceled,
}

public sealed record RequestMessage(DateTimeOffset At, string Text);

/// <summary>Trainer details read from a Pokémon the partner showed. They describe the Pokémon's original trainer.</summary>
public sealed record TrainerDetails(
    string Name,
    string Gender,
    string Tid,
    string Sid,
    string Game,
    string Language,
    string Pokemon,
    bool Shiny,
    bool Legal);

public sealed record TradeRequestView(
    string Id,
    RequestKind Kind,
    string User,
    string Code,
    string? Pokemon,
    RequestStatus Status,
    string? Outcome,
    string? Partner,
    string? Received,
    IReadOnlyList<RequestMessage> Messages,
    IReadOnlyList<TrainerDetails> Trainers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsActive => Status is RequestStatus.Queued or RequestStatus.Starting or RequestStatus.Searching;
}

/// <summary>
/// Every web request and what happened to it. Active requests live in memory; finished ones are kept
/// in <c>requests.json</c> (most recent <see cref="MaxFinished"/>) so trainer details survive a restart.
/// </summary>
public sealed class RequestRegistry
{
    public const int MaxFinished = 200;
    private const int MaxMessages = 40;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _sync = new();
    private readonly List<Entry> _entries = [];
    private readonly TimeProvider _time;
    private readonly string? _historyFile;

    public RequestRegistry(TimeProvider time, string? historyFile)
    {
        _time = time;
        _historyFile = historyFile;
        Load();
    }

    public TradeRequestView Create(RequestKind kind, string user, ulong userId, int code, string? pokemon)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            var entry = new Entry
            {
                Id = "rq_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5)),
                Kind = kind,
                User = user,
                UserId = userId,
                Code = FormatCode(code),
                Pokemon = pokemon,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _entries.Add(entry);
            return entry.View();
        }
    }

    public TradeRequestView? Get(string id)
    {
        lock (_sync)
        {
            return Find(id)?.View();
        }
    }

    /// <summary>Most recent first. Pass a user id to see only that user's requests.</summary>
    public IReadOnlyList<TradeRequestView> List(ulong? userId = null)
    {
        lock (_sync)
        {
            return [.. _entries.Where(e => userId is null || e.UserId == userId).OrderByDescending(e => e.CreatedAt).Select(e => e.View())];
        }
    }

    public bool OwnedBy(string id, ulong userId)
    {
        lock (_sync)
        {
            return Find(id)?.UserId == userId;
        }
    }

    public void SetStatus(string id, RequestStatus status, string? message = null) => Update(id, e =>
    {
        // Never move a finished request back to an active state (a late notification after a cancel).
        if (e.Status is RequestStatus.Finished or RequestStatus.Canceled && status is not RequestStatus.Finished and not RequestStatus.Canceled)
        {
            return;
        }

        e.Status = status;
    }, message);

    public void AddMessage(string id, string message) => Update(id, _ => { }, message);

    public void SetPartner(string id, string partner) => Update(id, e => e.Partner = partner);

    public void AddTrainer(string id, TrainerDetails trainer) => Update(id, e => e.Trainers.Add(trainer));

    public void Finish(string id, RequestStatus status, string outcome, string? received = null) => Update(id, e =>
    {
        e.Status = status;
        e.Outcome = outcome;
        e.Received = received ?? e.Received;
    }, outcome);

    private void Update(string id, Action<Entry> change, string? message = null)
    {
        bool finished;
        lock (_sync)
        {
            var entry = Find(id);
            if (entry is null)
            {
                return;
            }

            var wasActive = entry.IsActive;
            change(entry);
            var now = _time.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(message))
            {
                entry.Messages.Add(new RequestMessage(now, message.Trim()));
                if (entry.Messages.Count > MaxMessages)
                {
                    entry.Messages.RemoveAt(0);
                }
            }

            entry.UpdatedAt = now;
            finished = wasActive && !entry.IsActive;
            if (finished || !entry.IsActive)
            {
                Trim();
            }
        }

        if (finished)
        {
            Save();
        }
    }

    public static string FormatCode(int code) => code.ToString("0000 0000", System.Globalization.CultureInfo.InvariantCulture);

    private Entry? Find(string id) => _entries.Find(e => e.Id == id);

    private void Trim()
    {
        var finished = _entries.Where(e => !e.IsActive).OrderByDescending(e => e.UpdatedAt).Skip(MaxFinished).ToList();
        foreach (var old in finished)
        {
            _entries.Remove(old);
        }
    }

    private void Load()
    {
        if (_historyFile is null || !File.Exists(_historyFile))
        {
            return;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_historyFile), Json) ?? [];
            foreach (var entry in saved)
            {
                // Anything still active when the service stopped was lost with the in-memory queue.
                if (entry.IsActive)
                {
                    entry.Status = RequestStatus.Canceled;
                    entry.Outcome = "The trade service restarted before this request ran.";
                }

                _entries.Add(entry);
            }
        }
        catch (JsonException)
        {
            // A damaged history file is not worth refusing to start over; it is replaced on the next save.
        }
    }

    private void Save()
    {
        if (_historyFile is null)
        {
            return;
        }

        string json;
        lock (_sync)
        {
            json = JsonSerializer.Serialize(_entries.Where(e => !e.IsActive).OrderByDescending(e => e.UpdatedAt).ToList(), Json);
        }

        var temp = _historyFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _historyFile, overwrite: true);
    }

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public RequestKind Kind { get; set; }
        public string User { get; set; } = "";
        public ulong UserId { get; set; }
        public string Code { get; set; } = "";
        public string? Pokemon { get; set; }
        public RequestStatus Status { get; set; }
        public string? Outcome { get; set; }
        public string? Partner { get; set; }
        public string? Received { get; set; }
        public List<RequestMessage> Messages { get; set; } = [];
        public List<TrainerDetails> Trainers { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonIgnore]
        public bool IsActive => Status is RequestStatus.Queued or RequestStatus.Starting or RequestStatus.Searching;

        public TradeRequestView View() =>
            new(Id, Kind, User, Code, Pokemon, Status, Outcome, Partner, Received, [.. Messages], [.. Trainers], CreatedAt, UpdatedAt);
    }
}
