using ListHero.LiveBackup;

return await BackupCommand.RunAsync(args, Environment.GetEnvironmentVariable, Console.Out);
