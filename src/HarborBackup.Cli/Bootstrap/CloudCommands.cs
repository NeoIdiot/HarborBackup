using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Cli;

internal static class CloudCommands
{
    internal static int Run(string[] args)
    {
        var options = new Dictionary<string,string>();
        for(var i=1;i<args.Length;i++)
        {
            if(args[i]=="--json") continue;
            var key=args[i];
            if(key is not ("--config" or "--system-config" or "--client-id" or "--parent-id" or "--name" or "--project-id" or "--destination" or "--path" or "--prefer" or "--expected"))
                throw new ArgumentException("Unknown account option: "+key);
            if(++i==args.Length || !options.TryAdd(key,args[i]))throw new ArgumentException("Missing or duplicate option.");
        }
        string Required(string name)=>options.GetValueOrDefault(name)??throw new ArgumentException("Required: "+name);
        if (options.ContainsKey("--project-id") && options.ContainsKey("--destination")) throw new ArgumentException("Choose one project selector.");
        if (options.TryGetValue("--project-id", out var explicitId) && !FileVersion.IsHash(explicitId)) throw new ArgumentException("Invalid project ID.");
        var paths=ApplicationPaths.Resolve(options.GetValueOrDefault("--config"),options.GetValueOrDefault("--system-config"));
        using var guard=args[0] is "cloud-connect" or "cloud-bind" or "cloud-disconnect" or "cloud-upgrade" ? null : new RunLock();using var cancellation=new CancellationTokenSource();
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
        var store=new CloudAccountStore(paths.Data);var account=store.Read();var token=cancellation.Token;
        object result;
        switch(args[0])
        {
            case "cloud-connect": result=CloudConnection.ConnectAsync(paths.Data,options.GetValueOrDefault("--client-id"),token).GetAwaiter().GetResult();break;
            case "cloud-upgrade":
                if (!account.Enabled || string.IsNullOrWhiteSpace(account.RootId))
                    throw new InvalidOperationException("Bind a repository before upgrading.");
                result = new CloudAccountSession(paths.Data).ApplyAsync(account,
                    new(false,account.RootId,"",account.RootName,account.Folders ?? new(),
                        account.ArchiveRetention,account.ConflictRetention),token).GetAwaiter().GetResult();
                break;
            case "cloud-bind":
                using(var client=new GraphDrive(store.Tokens(account),account.DriveId??""))
                {
                    var parent=options.GetValueOrDefault("--parent-id")??client.RootAsync(token).GetAwaiter().GetResult().Id;
                    result=CloudConnection.BindAsync(paths.Data,parent,options.GetValueOrDefault("--name","HarborBackup"),token).GetAwaiter().GetResult();
                } break;
            case "cloud-folders":
                using(var client=new GraphDrive(store.Tokens(account),account.DriveId??""))
                {
                    var parent=options.GetValueOrDefault("--parent-id")??client.RootAsync(token).GetAwaiter().GetResult().Id;
                    result=client.ChildrenAsync(parent,token).GetAwaiter().GetResult().Where(i=>i.Folder).ToArray();
                }break;
            case "cloud-disconnect": new CloudAccountSession(paths.Data).DisconnectAsync(token).GetAwaiter().GetResult();result=new{status="AuthorizationRemoved",transportRemainsAccountBound=true};break;
            case "cloud-status":result=account;break;
            case "cloud-inspect":
            case "cloud-resolve":
                var config=new BackupRuntime(paths).Load();
                if(config.CloudAccount is null)throw new ArgumentException("Account mode is not enabled.");
                var selector = options.GetValueOrDefault("--project-id") ?? Required("--destination");
                var projectId = BackupRuntime.ResolveProjectId(config, selector)!;
                using(var client=store.Open())
                {
                    var inspection=CloudConflictResolution.InspectAsync(config,projectId,Required("--path"),client,token).GetAwaiter().GetResult();
                    if(args[0]=="cloud-resolve")
                    {
                        if(Required("--expected")!=inspection.Confirmation)throw new IOException("Versions changed. Inspect again.");
                        CloudConflictResolution.ResolveAsync(config,inspection,Required("--prefer"),client,token).GetAwaiter().GetResult();
                        result=new{status="ResolutionCompleted"};
                    }
                    else result=inspection;
                }break;
            default:throw new ArgumentException("Unknown cloud command.");
        }
        Console.WriteLine(JsonSerializer.Serialize(result,AtomicJson.Options));return 0;
    }
}
