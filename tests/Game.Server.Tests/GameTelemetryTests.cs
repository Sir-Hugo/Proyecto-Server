using System.Diagnostics.Metrics;
using Game.Server.Observability;

namespace Game.Server.Tests;

public sealed class GameTelemetryTests
{
    [Fact]
    public void RecordsBoundedCountersHistogramsAndObservableGauges()
    {
        using var telemetry = new GameTelemetry();
        Dictionary<string, List<double>> measurements = [];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GameTelemetry.MeterName) meterListener.EnableMeasurementEvents(instrument);
        };
        void Add(string name, double value)
        {
            if (!measurements.TryGetValue(name, out List<double>? values)) measurements[name] = values = [];
            values.Add(value);
        }
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => Add(instrument.Name, measurement));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) => Add(instrument.Name, measurement));
        listener.Start();

        telemetry.SetActiveMatches(2);
        telemetry.SetActiveConnections(3);
        telemetry.RecordRoundPlanProcessed(12.5);
        telemetry.RecordRoundResolved(4.25);
        telemetry.RecordFailure("game_operation", "persistence");
        listener.RecordObservableInstruments();

        Assert.Equal(1, Assert.Single(measurements["game.round_plans.processed"]));
        Assert.Equal(12.5, Assert.Single(measurements["game.round_plan.processing.duration"]));
        Assert.Equal(4.25, Assert.Single(measurements["game.round.resolution.duration"]));
        Assert.Equal(1, Assert.Single(measurements["game.failures"]));
        Assert.Equal(2, Assert.Single(measurements["game.matches.active"]));
        Assert.Equal(3, Assert.Single(measurements["game.signalr.connections.active"]));
    }
}
