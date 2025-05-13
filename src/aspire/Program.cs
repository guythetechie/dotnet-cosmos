
var builder = DistributedApplication.CreateBuilder(args);

// Create Cosmos connection
var cosmosConnectionName = builder.Configuration["COSMOS_CONNECTION_NAME"] ?? "cosmos";
#pragma warning disable ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
var cosmos = builder.AddAzureCosmosDB(cosmosConnectionName)
                    .RunAsEmulator(emulator => emulator.WithLifetime(ContainerLifetime.Session));
#pragma warning restore ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Create Cosmos database for C# tests
var csharpCosmosDatabaseName = builder.Configuration["CSHARP_COSMOS_DATABASE_NAME"] ?? "csharp";
var csharpCosmosDatabase = cosmos.AddCosmosDatabase(csharpCosmosDatabaseName);

// Add C# test project
builder.AddProject<Projects.csharp>("csharp")
       .WithReference(cosmos)
       .WithReference(csharpCosmosDatabase)
       .WaitFor(csharpCosmosDatabase);

// Create Cosmos database for F# tests
var fsharpCosmosDatabaseName = builder.Configuration["FSHARP_COSMOS_DATABASE_NAME"] ?? "fsharp";
var fsharpCosmosDatabase = cosmos.AddCosmosDatabase(fsharpCosmosDatabaseName);

// Add F# test project
builder.AddProject<Projects.fsharp>("fsharp")
       .WithReference(cosmos)
       .WithReference(fsharpCosmosDatabase)
       .WaitFor(fsharpCosmosDatabase);

builder.Build().Run();
