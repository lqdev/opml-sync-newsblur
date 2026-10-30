namespace OpmlSyncNewsBlur.Functions

open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Extensions.Timer
open OpmlSyncNewsBlur

type Renew(sync: SyncEngine) =
    [<Function("Renew")>]
    member _.Run([<TimerTrigger("0 0 */6 * * *")>] timerInfo: TimerInfo) : Task =
        sync.Renew()
