﻿using Polly.Simmy.Outcomes;
using Polly.Telemetry;

namespace Polly.Core.Tests.Simmy.Outcomes;

public class ChaosOutcomeStrategyTests
{
    private readonly ResilienceStrategyTelemetry _telemetry;
    private readonly List<TelemetryEventArguments<object, object>> _args = [];
    private bool _onOutcomeInjectedExecuted;
    private bool _userDelegateExecuted;

    public ChaosOutcomeStrategyTests()
    {
        _telemetry = TestUtilities.CreateResilienceTelemetry(arg => _args.Add(arg));
        _onOutcomeInjectedExecuted = false;
        _userDelegateExecuted = false;
    }

    public static List<object[]> ResultCtorTestCases =>
        new()
        {
                new object[] { null!, "Value cannot be null. (Parameter 'options')", typeof(ArgumentNullException) },
                new object[]
                {
                    new ChaosOutcomeStrategyOptions<int>
                    {
                        InjectionRate = 1,
                    },
                    "Either Outcome or OutcomeGenerator is required.",
                    typeof(InvalidOperationException)
                },
        };

    [Theory]
    [MemberData(nameof(ResultCtorTestCases))]
#pragma warning disable xUnit1026 // Theory methods should use all of their parameters
    public void ResultInvalidCtor(object options, string expectedMessage, Type expectedException)
#pragma warning restore xUnit1026 // Theory methods should use all of their parameters
    {
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            var _ = new ChaosOutcomeStrategy<int>((ChaosOutcomeStrategyOptions<int>)options, _telemetry);
        }
        catch (Exception ex)
        {
            Assert.IsType(expectedException, ex);
#if !NET481
            Assert.Equal(expectedMessage, ex.Message);
#endif
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    [Fact]
    public void Given_not_enabled_should_not_inject_result()
    {
        var fakeResult = HttpStatusCode.TooManyRequests;

        var options = new ChaosOutcomeStrategyOptions<HttpStatusCode>
        {
            InjectionRate = 0.6,
            Enabled = false,
            Randomizer = () => 0.5,
            OutcomeGenerator = (_) => new ValueTask<Outcome<HttpStatusCode>?>(Outcome.FromResult(fakeResult))
        };

        var sut = CreateSut(options);
        var response = sut.Execute(() => { _userDelegateExecuted = true; return HttpStatusCode.OK; });

        response.Should().Be(HttpStatusCode.OK);
        _userDelegateExecuted.Should().BeTrue();
        _onOutcomeInjectedExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task Given_enabled_and_randomly_within_threshold_should_inject_result()
    {
        var fakeResult = HttpStatusCode.TooManyRequests;

        var options = new ChaosOutcomeStrategyOptions<HttpStatusCode>
        {
            InjectionRate = 0.6,
            Randomizer = () => 0.5,
            OutcomeGenerator = (_) => new ValueTask<Outcome<HttpStatusCode>?>(Outcome.FromResult(fakeResult)),
            OnOutcomeInjected = args =>
            {
                args.Context.Should().NotBeNull();
                args.Context.CancellationToken.IsCancellationRequested.Should().BeFalse();
                _onOutcomeInjectedExecuted = true;
                return default;
            }
        };

        var sut = CreateSut(options);
        var response = await sut.ExecuteAsync(async _ =>
        {
            _userDelegateExecuted = true;
            return await Task.FromResult(HttpStatusCode.OK);
        });

        response.Should().Be(fakeResult);
        _userDelegateExecuted.Should().BeFalse();
        _onOutcomeInjectedExecuted.Should().BeTrue();

        _args.Should().HaveCount(1);
        _args[0].Arguments.Should().BeOfType<OnOutcomeInjectedArguments<HttpStatusCode>>();
        _args[0].Event.EventName.Should().Be(ChaosOutcomeConstants.OnOutcomeInjectedEvent);
    }

    [Fact]
    public void Given_enabled_and_randomly_not_within_threshold_should_not_inject_result()
    {
        var fakeResult = HttpStatusCode.TooManyRequests;

        var options = new ChaosOutcomeStrategyOptions<HttpStatusCode>
        {
            InjectionRate = 0.3,
            Enabled = false,
            Randomizer = () => 0.5,
            OutcomeGenerator = (_) => new ValueTask<Outcome<HttpStatusCode>?>(Outcome.FromResult(fakeResult))
        };

        var sut = CreateSut(options);
        var response = sut.Execute(_ =>
        {
            _userDelegateExecuted = true;
            return HttpStatusCode.OK;
        });

        response.Should().Be(HttpStatusCode.OK);
        _userDelegateExecuted.Should().BeTrue();
        _onOutcomeInjectedExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task Given_enabled_and_randomly_within_threshold_should_inject_result_even_as_null()
    {
        Outcome<HttpStatusCode?>? nullOutcome = Outcome.FromResult<HttpStatusCode?>(null);
        var options = new ChaosOutcomeStrategyOptions<HttpStatusCode?>
        {
            InjectionRate = 0.6,
            Randomizer = () => 0.5,
            OutcomeGenerator = (_) => new ValueTask<Outcome<HttpStatusCode?>?>(nullOutcome)
        };

        var sut = CreateSut(options);
        var response = await sut.ExecuteAsync<HttpStatusCode?>(async _ =>
        {
            _userDelegateExecuted = true;
            return await Task.FromResult(HttpStatusCode.OK);
        });

        response.Should().Be(null);
        _userDelegateExecuted.Should().BeFalse();
        _onOutcomeInjectedExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task Should_not_execute_user_delegate_when_it_was_cancelled_running_the_strategy()
    {
        using var cts = new CancellationTokenSource();
        var options = new ChaosOutcomeStrategyOptions<HttpStatusCode>
        {
            InjectionRate = 0.6,
            Randomizer = () => 0.5,
            EnabledGenerator = (_) =>
            {
                cts.Cancel();
                return new ValueTask<bool>(true);
            },
            OutcomeGenerator = (_) => new ValueTask<Outcome<HttpStatusCode>?>(Outcome.FromResult(HttpStatusCode.TooManyRequests))
        };

        var sut = CreateSut(options);
        await sut.Invoking(s => s.ExecuteAsync(async _ =>
        {
            _userDelegateExecuted = true;
            return await Task.FromResult(HttpStatusCode.OK);
        }, cts.Token)
        .AsTask())
            .Should()
            .ThrowAsync<OperationCanceledException>();

        _userDelegateExecuted.Should().BeFalse();
        _onOutcomeInjectedExecuted.Should().BeFalse();
    }

    private ResiliencePipeline<TResult> CreateSut<TResult>(ChaosOutcomeStrategyOptions<TResult> options) =>
        new ChaosOutcomeStrategy<TResult>(options, _telemetry).AsPipeline();
}

/// <summary>
/// Borrowing this from the actual dotnet standard implementation since it is not available in the net481.
/// </summary>
public enum HttpStatusCode
{
    // Summary:
    //     Equivalent to HTTP status 200. System.Net.HttpStatusCode.OK indicates that the
    //     request succeeded and that the requested information is in the response. This
    //     is the most common status code to receive.
    OK = 200,

    // Summary:
    //     Equivalent to HTTP status 429. System.Net.HttpStatusCode.TooManyRequests indicates
    //     that the user has sent too many requests in a given amount of time.
    TooManyRequests = 429,
}
