using Microsoft.Testing.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raun;
using Raun.Mtp;

namespace AppointmentTests;

/// <summary>
/// Custom entry point for simulated step durations, scenario-scoped services, and TRX reports.
/// Resource mappings and application logging are registered here before the run starts.
/// Omit this file and RaunGenerateProgram=false when the generated defaults are enough.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // External clinic DTOs can share a typed identity or use a named resource kind.
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Slot>().With<Slot>().With<SlotRow>(row => Slot.KeyFor(new Slot(row.Hour)));
        resolver.Label<SlotRow>(row => $"Clinic slot at {row.Hour}:00");
        resolver.Identify("clinic-confirmation").With<DeliveryReceipt>(receipt => receipt.Id);
        resolver.Label<DeliveryReceipt>(receipt => $"Confirmation for {receipt.Recipient}");
        await using var services = new ServiceCollection()
            .AddSingleton(resolver)
            .AddScoped<ReceptionSession>()
            .AddLogging(logging => logging.AddProvider(new RaunLoggerProvider()))
            .BuildServiceProvider();
        return await RaunTestApplication.RunAsync(
            args,
            simulateTime: true,
            services: services,
            configure: builder => builder.AddTrxReportProvider());
    }
}
