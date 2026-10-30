namespace OpmlSyncNewsBlur.Functions

open System.Text.Json
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open OpmlSyncNewsBlur

type ReconcileQueue(sync: SyncEngine) =
    [<Function("ReconcileQueue")>]
    member _.Run([<QueueTrigger("opml-reconcile", Connection = "AzureWebJobsStorage")>] message: string) : Task =
        task {
            let reconcileMessage = JsonSerializer.Deserialize<ReconcileMessage>(message)

            if isNull (box reconcileMessage) then
                invalidOp "Queue message did not contain a reconciliation request."

            do! sync.Reconcile(reconcileMessage)
        }
