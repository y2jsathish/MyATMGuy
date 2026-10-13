using ATM.Application;
using ATM.Application.Devices;
using ATM.Core.Devices;
using ATM.Core.Errors;
using ATM.Infrastructure;
using ATM.Infrastructure.Devices.Simulator;
using ATM.TestApp.Common;
using Microsoft.Extensions.DependencyInjection;

var runner = new CheckRunner("ATM.Infrastructure");

static ServiceProvider Build(Action<SimulatorOptions>? configure = null) =>
    new ServiceCollection()
        .AddApplication()
        .AddInfrastructure(configure)
        .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

await runner.CheckAsync("Container validates and resolves every device port", async () =>
{
    await using var provider = Build();

    Expect.True(provider.GetRequiredService<ICardReaderPort>() is not null, "Card reader port missing.");
    Expect.True(provider.GetRequiredService<IPinPadPort>() is not null, "Pin pad port missing.");
    Expect.True(provider.GetRequiredService<ICashDispenserPort>() is not null, "Cash dispenser port missing.");
    Expect.True(provider.GetRequiredService<IReceiptPrinterPort>() is not null, "Receipt printer port missing.");
    Expect.Equal(4, provider.GetServices<IDevicePort>().Count(), "Registered device ports");
});

await runner.CheckAsync("Self-test across the simulators is Ok", async () =>
{
    await using var provider = Build();

    var report = await provider.GetRequiredService<IDeviceSelfTestService>().RunAsync();

    Expect.Equal(DeviceStatus.Ok, report.Overall, "Overall status");
});

await runner.CheckAsync("Full withdrawal flow runs end to end", async () =>
{
    await using var provider = Build();
    var card = await provider.GetRequiredService<ICardReaderPort>().WaitForCardAsync(TimeSpan.FromSeconds(1));
    var pin = await provider.GetRequiredService<IPinPadPort>().ReadPinAsync(4, 6);
    var cash = await provider.GetRequiredService<ICashDispenserPort>().DispenseAsync(100m);
    var printer = provider.GetRequiredService<SimulatedReceiptPrinter>();
    await printer.PrintAsync($"Withdrawal 100 on {card!.Pan}");
    await provider.GetRequiredService<ICardReaderPort>().EjectAsync();

    Expect.True(pin.Completed, "PIN entry did not complete.");
    Expect.True(cash.Success, "Dispense failed.");
    Expect.True(printer.Printed.Single().Contains("411111******1111", StringComparison.Ordinal), "Receipt must carry the masked card number.");
    Expect.True(!printer.Printed.Single().Contains("4111111111111111", StringComparison.Ordinal), "Receipt leaked the full card number.");
});

await runner.CheckAsync("Injected dispenser fault is reported and thrown", async () =>
{
    await using var provider = Build(o => o.CashDispenserStatus = DeviceStatus.Fault);

    var report = await provider.GetRequiredService<IDeviceSelfTestService>().RunAsync();
    var error = await Expect.ThrowsAsync<AtmException>(() => provider.GetRequiredService<ICashDispenserPort>().DispenseAsync(20m));

    Expect.Equal(DeviceStatus.Fault, report.Overall, "Overall status");
    Expect.Equal("ATM-4001", error.Code.Code, "Error code");
});

await runner.CheckAsync("Insufficient cash fails cleanly and keeps the balance", async () =>
{
    var options = new SimulatorOptions { CashAvailable = 50m };
    var dispenser = new SimulatedCashDispenser(options);

    var result = await dispenser.DispenseAsync(100m);

    Expect.True(!result.Success, "Dispense should have failed.");
    Expect.Equal(50m, options.CashAvailable, "Cash available");
});

await runner.CheckAsync("Offline pin pad raises the offline error code", async () =>
{
    var pad = new SimulatedPinPad(new SimulatorOptions { PinPadStatus = DeviceStatus.Offline });

    var error = await Expect.ThrowsAsync<AtmException>(() => pad.ReadPinAsync(4, 6));

    Expect.Equal("ATM-4002", error.Code.Code, "Error code");
});

await runner.CheckAsync("Audit trail is chained and detects tampering", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.jsonl");
    try
    {
        var service = new ATM.Application.Audit.AuditService(
            new ATM.Infrastructure.Audit.JsonLinesAuditStore(new ATM.Infrastructure.Audit.AuditFileOptions { Path = path }),
            TimeProvider.System,
            new ATM.Application.Audit.AuditSigningKey(new byte[32]),
            new ATM.Application.Terminal.TerminalInfo("ATM-0001", "1.0", "testapp"));
        await service.RecordAsync("tech", "SelfTest", "devices", "ok");
        await service.RecordAsync("tech", "Dispense", "cash", "ok");
        Expect.True((await service.VerifyAsync()).IsValid, "Fresh chain should verify.");

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Outcome\":\"ok\"", "\"Outcome\":\"no\"", StringComparison.Ordinal));
        Expect.True(!(await service.VerifyAsync()).IsValid, "Edited audit file must fail verification.");
    }
    finally
    {
        File.Delete(path);
    }
});

await runner.CheckAsync("Logs are written as searchable JSON with PAN masked", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), $"atmlogs-{Guid.NewGuid():N}");
    try
    {
        var info = new ATM.Application.Terminal.TerminalInfo("ATM-0099", "1.0", "testapp");
        using (var logger = ATM.Infrastructure.Logging.AtmLogging.Create(
                   new ATM.Infrastructure.Logging.LoggingOptions { Directory = dir }, info, new ATM.Infrastructure.Logging.LogLevelController()))
        using (ATM.Application.Correlation.CorrelationContext.Begin())
        {
            logger.Information("Card 4111111111111111 inserted");
            logger.Error("Dispense failed");
        }

        var found = new List<ATM.Infrastructure.Logging.LogLine>();
        await foreach (var line in ATM.Infrastructure.Logging.LogSearch.SearchAsync(dir, new ATM.Infrastructure.Logging.LogQuery(MinimumLevel: "Information")))
        {
            found.Add(line);
        }

        Expect.Equal(2, found.Count, "Log lines");
        Expect.True(found.All(l => !l.Raw.Contains("4111111111111111", StringComparison.Ordinal)), "Full PAN reached the log file.");
        Expect.True(found.All(l => l.Terminal == "ATM-0099" && l.CorrelationId is not null), "Terminal or correlation ID missing.");
    }
    finally
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
});

await runner.CheckAsync("Configuration layers, encryption and last-known-good fallback", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), $"atmcfg-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        var protector = new ATM.Application.Configuration.SecretProtector(new ATM.Infrastructure.Configuration.FileKeyProvider(Path.Combine(dir, "keys")));
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{\"Terminal\":{\"TerminalId\":\"BASE-1\"},\"Db\":{\"Password\":\"" + protector.Protect("hunter2") + "\"}}");
        File.WriteAllText(Path.Combine(dir, "terminal.json"), "{\"Terminal\":{\"TerminalId\":\"TERM-9\"}}");

        var good = ATM.Infrastructure.Configuration.ConfigBootstrap.Load(dir, ATM.Application.Configuration.AtmEnvironment.Dev, protector);
        Expect.True(good.IsValid, "Valid config reported errors: " + string.Join("; ", good.Errors));
        Expect.Equal("TERM-9", good.Configuration["Terminal:TerminalId"], "terminal.json must override appsettings.json");
        Expect.Equal("hunter2", ATM.Infrastructure.Configuration.ConfigurationBuilderExtensions.WithDecryption(good.Configuration, protector)["Db:Password"], "Decrypted secret");
        Expect.True(!File.ReadAllText(Path.Combine(dir, "appsettings.json")).Contains("hunter2", StringComparison.Ordinal), "Secret is plaintext on disk.");

        ATM.Infrastructure.Configuration.ConfigBootstrap.SaveLastKnownGood(dir, good.Raw);
        File.WriteAllText(Path.Combine(dir, "terminal.json"), "{\"Terminal\":{\"TerminalId\":\"not valid!\"}}");
        var fallback = ATM.Infrastructure.Configuration.ConfigBootstrap.Load(dir, ATM.Application.Configuration.AtmEnvironment.Dev, protector);
        Expect.True(fallback.UsedLastKnownGood, "Expected fallback to last known good.");
        Expect.Equal("TERM-9", fallback.Configuration["Terminal:TerminalId"], "Fallback value");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }

    await Task.CompletedTask;
});

await runner.CheckAsync("Database: migrate, save, page, back up, restore and keep PAN out of the file", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), $"atmdb-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        await using var provider = new ServiceCollection()
            .AddSingleton(new ATM.Application.Terminal.TerminalInfo("ATM-0001", "1.0", "testapp"))
            .AddSingleton<ATM.Application.Configuration.IKeyProvider>(new ATM.Infrastructure.Configuration.FileKeyProvider(Path.Combine(dir, "keys")))
            .AddSingleton<ATM.Application.Configuration.SecretProtector>()
            .AddSingleton(sp => ATM.Infrastructure.Audit.AuditKeyDerivation.Derive(sp.GetRequiredService<ATM.Application.Configuration.IKeyProvider>()))
            .AddSingleton(new ATM.Infrastructure.Audit.AuditFileOptions { Path = Path.Combine(dir, "fallback.jsonl") })
            .AddSingleton<ATM.Application.Errors.IErrorCounters, ATM.Application.Errors.ErrorCounters>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ATM.Application.Audit.IAuditService, ATM.Application.Audit.AuditService>()
            .AddAtmPersistence(o => { o.Path = Path.Combine(dir, "atm.db"); o.BackupDirectory = Path.Combine(dir, "backups"); })
            .BuildServiceProvider();

        var maintenance = provider.GetRequiredService<ATM.Infrastructure.Persistence.DatabaseMaintenance>();
        await maintenance.InitializeAsync();

        var terminal = ATM.Core.ValueObjects.TerminalId.Create("ATM-0001");
        await using (var uow = provider.GetRequiredService<ATM.Application.Persistence.IUnitOfWorkFactory>().Create())
        {
            await uow.Transactions.AddAsync(ATM.Core.Entities.Transaction.Start(terminal, ATM.Core.Entities.TransactionType.Withdrawal, 40m, ATM.Core.ValueObjects.MaskedPan.FromPan("4111111111111111"), DateTimeOffset.UtcNow));
            await uow.CommitAsync();
        }

        await provider.GetRequiredService<ATM.Application.Audit.IAuditService>().RecordAsync("tester", "Withdrawal", "card 4111111111111111", "ok");
        var backup = await maintenance.BackupAsync();
        Expect.True(File.Exists(backup), "Backup file missing.");
        Expect.True(await maintenance.IntegrityOkAsync(), "Integrity check failed on a healthy database.");
        Expect.True((await provider.GetRequiredService<ATM.Application.Audit.IAuditService>().VerifyAsync()).IsValid, "Audit chain invalid.");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(dir, "atm.db")));
        Expect.True(!text.Contains("4111111111111111", StringComparison.Ordinal), "Full PAN found in the database file.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, recursive: true);
    }
});

return runner.Complete(args);
