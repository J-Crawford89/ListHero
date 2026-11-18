using ListHero.Infrastructure.Maintenance;
return await MaintenanceCommand.RunAsync(args, Environment.GetEnvironmentVariable, Console.Out);
