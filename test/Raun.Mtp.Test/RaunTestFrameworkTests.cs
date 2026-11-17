using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.TestHost;
using Raun.Mtp;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Behavioral tests for the MTP <c>ITestFramework</c> shell: session create/close
/// lifecycle (open, close, duplicate-open, double-close, unknown-close) and the base
/// <c>ExecuteRequestAsync</c> routing contract (discover vs run routing to the right handler).
/// </summary>
public static class RaunTestFrameworkTests
{
    public class SessionManagement
    {
        [Fact]
        public async Task Create_then_close_session_succeeds()
        {
            var uid = new SessionUid("session-1");
            var framework = new RaunTestFramework();

            var createResult = await framework.CreateTestSession(uid);
            Assert.True(createResult.IsSuccess);

            var closeResult = await framework.CloseTestSession(uid);
            Assert.True(closeResult.IsSuccess);
        }

        [Fact]
        public async Task Creating_same_session_twice_fails()
        {
            var uid = new SessionUid("dup");
            var framework = new RaunTestFramework();

            var first = await framework.CreateTestSession(uid);
            var second = await framework.CreateTestSession(uid);

            Assert.True(first.IsSuccess);
            Assert.False(second.IsSuccess);
            Assert.Contains("dup", second.ErrorMessage);
        }

        [Fact]
        public async Task Closing_unknown_session_fails()
        {
            var framework = new RaunTestFramework();

            var result = await framework.CloseTestSession(new SessionUid("never-opened"));

            Assert.False(result.IsSuccess);
            Assert.Contains("never-opened", result.ErrorMessage);
        }

        [Fact]
        public async Task Re_closing_a_session_fails()
        {
            var uid = new SessionUid("once");
            var framework = new RaunTestFramework();

            await framework.CreateTestSession(uid);
            var firstClose = await framework.CloseTestSession(uid);
            var secondClose = await framework.CloseTestSession(uid);

            Assert.True(firstClose.IsSuccess);
            Assert.False(secondClose.IsSuccess);
        }

        [Fact]
        public async Task Discovery_against_unknown_session_throws_and_does_not_complete()
        {
            var framework = new RaunTestFramework();
            var completed = false;

            var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await framework.OnDiscover(
                    new SessionUid("ghost"),
                    filter: null,
                    new SpyMessageBus(),
                    () => completed = true,
                    CancellationToken.None));

            Assert.False(completed);
            Assert.Contains("ghost", ex.Message);
        }

        [Fact]
        public async Task Execution_against_unknown_session_throws_and_does_not_complete()
        {
            var framework = new RaunTestFramework();
            var completed = false;

            var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await framework.OnExecute(
                    new SessionUid("ghost"),
                    filter: null,
                    new SpyMessageBus(),
                    () => completed = true,
                    CancellationToken.None));

            Assert.False(completed);
            Assert.Contains("ghost", ex.Message);
        }
    }

    private sealed class SpyMessageBus : IMessageBus
    {
        public List<IData> Published { get; } = [];

        public Task PublishAsync(IDataProducer dataProducer, IData data)
        {
            Published.Add(data);
            return Task.CompletedTask;
        }
    }

}
