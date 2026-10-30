namespace OpmlSyncNewsBlur

open System
open System.Threading
open System.Threading.Tasks
open Azure.Data.Tables
open Azure.Identity
open Azure.Storage.Blobs
open Azure.Storage.Queues
open Microsoft.Azure.Functions.Worker
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open System.Net.Http

type StorageInitializer(tables: Storage.TableStore, queues: Storage.QueueStore, blobs: Storage.BlobStore) =
    interface IHostedService with
        member _.StartAsync(_: CancellationToken) =
            task {
                do! tables.EnsureTables()
                do! queues.EnsureQueues()
                do! blobs.EnsureContainer()
            }
            :> Task

        member _.StopAsync(_: CancellationToken) = Task.CompletedTask

module Program =
    let private setting name =
        Environment.GetEnvironmentVariable(name) |> Option.ofObj

    let private storageCredential () =
        let clientId = setting "AzureWebJobsStorage__clientId" |> Option.defaultValue null
        DefaultAzureCredential(DefaultAzureCredentialOptions(ManagedIdentityClientId = clientId))

    let private tableClient () =
        match setting "AzureWebJobsStorage" with
        | Some connection when not (String.IsNullOrWhiteSpace(connection)) -> TableServiceClient(connection)
        | _ ->
            let uri =
                setting "AzureWebJobsStorage__tableServiceUri"
                |> Option.defaultWith (fun () -> invalidOp "Missing AzureWebJobsStorage__tableServiceUri.")
                |> Uri
            TableServiceClient(uri, storageCredential ())

    let private queueClient () =
        match setting "AzureWebJobsStorage" with
        | Some connection when not (String.IsNullOrWhiteSpace(connection)) -> QueueServiceClient(connection)
        | _ ->
            let uri =
                setting "AzureWebJobsStorage__queueServiceUri"
                |> Option.defaultWith (fun () -> invalidOp "Missing AzureWebJobsStorage__queueServiceUri.")
                |> Uri
            QueueServiceClient(uri, storageCredential ())

    let private blobClient () =
        match setting "AzureWebJobsStorage" with
        | Some connection when not (String.IsNullOrWhiteSpace(connection)) -> BlobServiceClient(connection)
        | _ ->
            let uri =
                setting "AzureWebJobsStorage__blobServiceUri"
                |> Option.defaultWith (fun () -> invalidOp "Missing AzureWebJobsStorage__blobServiceUri.")
                |> Uri
            BlobServiceClient(uri, storageCredential ())

    [<EntryPoint>]
    let main _ =
        let config = Config.load ()

        HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .ConfigureServices(fun services ->
                services.AddSingleton<SyncConfig>(config) |> ignore
                services.AddSingleton<TableServiceClient>(tableClient ()) |> ignore
                services.AddSingleton<QueueServiceClient>(queueClient ()) |> ignore
                services.AddSingleton<BlobServiceClient>(blobClient ()) |> ignore
                services.AddSingleton<Storage.TableStore>() |> ignore
                services.AddSingleton<Storage.QueueStore>() |> ignore
                services.AddSingleton<Storage.BlobStore>() |> ignore
                services.AddSingleton<StorageInitializer>() |> ignore
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun provider ->
                        provider.GetRequiredService<StorageInitializer>() :> Microsoft.Extensions.Hosting.IHostedService)
                )
                |> ignore

                services.AddHttpClient("websub", fun client ->
                    client.Timeout <- TimeSpan.FromSeconds(30.0))
                    .ConfigurePrimaryHttpMessageHandler(
                        Func<System.Net.Http.HttpMessageHandler>(fun () ->
                            new System.Net.Http.HttpClientHandler(AllowAutoRedirect = false))
                    )
                |> ignore

                services.AddHttpClient("newsblur", fun client ->
                    client.BaseAddress <- Uri("https://www.newsblur.com/")
                    client.Timeout <- TimeSpan.FromSeconds(30.0))
                    .ConfigurePrimaryHttpMessageHandler(
                        Func<System.Net.Http.HttpMessageHandler>(fun () ->
                            new System.Net.Http.HttpClientHandler(UseCookies = true, AllowAutoRedirect = false))
                    )
                |> ignore

                services.AddSingleton<HubClient>(fun provider ->
                    HubClient(
                        config,
                        provider.GetRequiredService<IHttpClientFactory>().CreateClient("websub")
                    ))
                |> ignore

                services.AddSingleton<NewsBlurClient>(fun provider ->
                    NewsBlurClient(
                        config,
                        provider.GetRequiredService<IHttpClientFactory>().CreateClient("newsblur")
                    ))
                |> ignore

                services.AddSingleton<SyncEngine>(fun provider ->
                    SyncEngine(
                        config,
                        provider.GetRequiredService<Storage.TableStore>(),
                        provider.GetRequiredService<Storage.BlobStore>(),
                        provider.GetRequiredService<HubClient>(),
                        provider.GetRequiredService<NewsBlurClient>(),
                        provider.GetRequiredService<IHttpClientFactory>().CreateClient("websub"),
                        provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SyncEngine>>()
                    ))
                |> ignore

                services.AddApplicationInsightsTelemetryWorkerService() |> ignore
                services.ConfigureFunctionsApplicationInsights() |> ignore)
            .Build()
            .Run()

        0
