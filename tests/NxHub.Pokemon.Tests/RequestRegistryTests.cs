using NxHub.Pokemon.Requests;

namespace NxHub.Pokemon.Tests;

public sealed class RequestRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nxhub-pokemon-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Create_formats_the_code_and_starts_queued()
    {
        var registry = new RequestRegistry(TimeProvider.System, null);
        var request = registry.Create(RequestKind.Trade, "sam", 1, 8180, "Pikachu");

        Assert.StartsWith("rq_", request.Id, StringComparison.Ordinal);
        Assert.Equal("0000 8180", request.Code);
        Assert.Equal(RequestStatus.Queued, request.Status);
        Assert.True(request.IsActive);
    }

    [Fact]
    public void Users_only_see_their_own_requests()
    {
        var registry = new RequestRegistry(TimeProvider.System, null);
        registry.Create(RequestKind.Trade, "sam", 1, 1, null);
        registry.Create(RequestKind.TrainerInfo, "alex", 2, 2, null);

        Assert.Single(registry.List(1));
        Assert.Equal(2, registry.List().Count);
    }

    [Fact]
    public void A_finished_request_is_not_reopened_by_a_late_notification()
    {
        var registry = new RequestRegistry(TimeProvider.System, null);
        var id = registry.Create(RequestKind.Trade, "sam", 1, 1, null).Id;

        registry.Finish(id, RequestStatus.Canceled, "Stopped.");
        registry.SetStatus(id, RequestStatus.Searching, "Searching now.");

        var request = registry.Get(id)!;
        Assert.Equal(RequestStatus.Canceled, request.Status);
        Assert.Equal("Stopped.", request.Outcome);
        Assert.Equal(2, request.Messages.Count);
    }

    [Fact]
    public void Finished_requests_and_trainer_details_survive_a_restart()
    {
        var file = Path.Combine(_dir, "requests.json");
        var registry = new RequestRegistry(TimeProvider.System, file);
        var done = registry.Create(RequestKind.TrainerInfo, "sam", 1, 1234_5678, null).Id;
        registry.AddTrainer(done, new TrainerDetails("Sam", "Male", "123456", "7890", "Scarlet", "ENG", "Pikachu", false, true));
        registry.Finish(done, RequestStatus.Finished, "Trainer details read.");
        registry.Create(RequestKind.Trade, "sam", 1, 1, "Eevee");

        var reloaded = new RequestRegistry(TimeProvider.System, file);

        var request = Assert.Single(reloaded.List());
        Assert.Equal(done, request.Id);
        Assert.Equal("123456", Assert.Single(request.Trainers).Tid);
    }

    [Fact]
    public void Only_the_most_recent_finished_requests_are_kept()
    {
        var registry = new RequestRegistry(TimeProvider.System, null);
        for (var i = 0; i < RequestRegistry.MaxFinished + 5; i++)
        {
            var id = registry.Create(RequestKind.Trade, "sam", 1, i, null).Id;
            registry.Finish(id, RequestStatus.Finished, "Done.");
        }

        var active = registry.Create(RequestKind.Trade, "sam", 1, 1, null);

        Assert.Equal(RequestRegistry.MaxFinished + 1, registry.List().Count);
        Assert.NotNull(registry.Get(active.Id));
    }
}
