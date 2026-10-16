using System.ComponentModel;
using System.Net;
using Raun;
using Xunit;

namespace AppointmentTests;

public enum EndpointRole { Anonymous, Reader, Admin }

public sealed class AuthorizationWorld : IScenarioWorld<AuthorizationWorld>
{
    public static ValueTask<AuthorizationWorld> CreateAsync(ScenarioContext context)
        => ValueTask.FromResult(new AuthorizationWorld());

    private readonly Dictionary<string, EndpointRole> _requiredRoles = new(StringComparer.Ordinal)
    {
        ["/appointments"] = EndpointRole.Reader,
        ["/admin/appointments"] = EndpointRole.Admin,
    };

    // A deterministic local endpoint handler keeps the example runnable without an HTTP server.
    public HttpStatusCode Request(string endpoint, EndpointRole role)
    {
        if (role == EndpointRole.Anonymous) return HttpStatusCode.Unauthorized;
        if (!_requiredRoles.TryGetValue(endpoint, out var required)) return HttpStatusCode.NotFound;
        return role >= required ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
    }
}

public sealed class AuthorizationWhen : When<AuthorizationWorld>
{
    [StepName("When {role} requests {endpoint}")]
    public Task<HttpStatusCode> Request(string endpoint, EndpointRole role)
    {
        var status = World.Request(endpoint, role);
        Context.Log($"{endpoint}: {(int)status} {status}");
        return Task.FromResult(status);
    }
}

public sealed class AuthorizationThen : Then<AuthorizationWorld>
{
    [StepName("Then the status is {expected}")]
    public Task StatusIs(HttpStatusCode actual, HttpStatusCode expected)
    {
        Assert.Equal(expected, actual);
        return Task.CompletedTask;
    }
}

public abstract class AuthorizationSuite : Scenarios<AuthorizationWorld>
{
    public AuthorizationWhen When => Steps<AuthorizationWhen>();
    public AuthorizationThen Then => Steps<AuthorizationThen>();
}

[DisplayName("Endpoint authorization")]
public sealed class AuthorizationScenarios : AuthorizationSuite
{
    [Scenario("endpoint access")]
    [ScenarioCase("anonymous", "/appointments", EndpointRole.Anonymous, HttpStatusCode.Unauthorized)]
    [ScenarioCase("reader", "/appointments", EndpointRole.Reader, HttpStatusCode.OK)]
    [ScenarioCase("reader cannot administer", "/admin/appointments", EndpointRole.Reader, HttpStatusCode.Forbidden)]
    [ScenarioCase("admin", "/admin/appointments", EndpointRole.Admin, HttpStatusCode.OK)]
    public async Task EndpointAccess(string endpoint, EndpointRole role, HttpStatusCode expected)
    {
        var status = await When.Request(endpoint, role);
        await Then.StatusIs(status, expected);
    }
}
