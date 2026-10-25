using Telemetrix.Transformer;

await using var app = TransformerApplication.Build(args);
await app.RunAsync();
