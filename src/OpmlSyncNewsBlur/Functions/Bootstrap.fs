namespace OpmlSyncNewsBlur.Functions

open System.Net
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Http
open OpmlSyncNewsBlur

type Bootstrap(sync: SyncEngine) =
    [<Function("Bootstrap")>]
    member _.Run([<HttpTrigger(AuthorizationLevel.Function, "post", Route = "admin/bootstrap")>] request: HttpRequestData) : Task<HttpResponseData> =
        task {
            do! sync.Bootstrap()
            let response = request.CreateResponse(HttpStatusCode.OK)
            do! response.WriteStringAsync("Bootstrap reconciliation completed.")
            return response
        }
