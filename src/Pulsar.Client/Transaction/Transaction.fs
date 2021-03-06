namespace Pulsar.Client.Transaction

open System
open System.Collections.Concurrent
open System.Collections.Generic
open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Threading.Tasks
open Pulsar.Client.Common
open Pulsar.Client.Common.UMX
open Pulsar.Client.Internal
open Microsoft.Extensions.Logging

type TxnId = {
    MostSigBits: uint64
    LeastSigBits: uint64
}
with
    override this.ToString() =
        $"{this.MostSigBits}:{this.LeastSigBits}"

type TxnOperations =
    {
        AddPublishPartitionToTxn: TxnId * CompleteTopicName -> Task<unit>
        AddSubscriptionToTxn: TxnId * CompleteTopicName * SubscriptionName -> Task<unit>
        Commit: TxnId * MessageId seq -> Task<unit>
        Abort: TxnId * MessageId seq -> Task<unit>
    }
    
type ConsumerTxnOperations =
    {
        ClearIncomingMessagesAndGetMessageNumber: unit -> Async<int>
        IncreaseAvailablePermits: int -> unit
    }

[<AllowNullLiteral>]
type Transaction internal (timeout: TimeSpan, txnOperations: TxnOperations, txnId: TxnId) as this =
    
    let producedTopics = ConcurrentDictionary<CompleteTopicName, Task<unit>>()
    let ackedTopics = ConcurrentDictionary<CompleteTopicName * SubscriptionName, Task<unit>>()
    let sendTasks = ResizeArray<Task<MessageId>>()
    let ackTasks = ResizeArray<Task<Unit>>()
    let cumulativeAckConsumers = Dictionary<ConsumerId, ConsumerTxnOperations>()
    let mutable allowOperations = true
    let lockObj = Object()
    
    let allOpComplete() =
        seq {
            for sendTask in sendTasks do
                yield (sendTask :> Task)
            for ackTask in ackTasks do
                yield (ackTask :> Task)
        } |> Task.WhenAll
        
    let executeInsideLock f errorMsg =
        if allowOperations then
            lock lockObj (fun () ->
                if allowOperations then
                    f()
                else
                    failwith errorMsg
            )
        else
            failwith errorMsg
            
    do asyncDelay timeout (fun () ->
            if allowOperations then
                try
                    this.Abort().GetAwaiter().GetResult()
                with ex ->
                    Log.Logger.LogError(ex, "Failure while aborting txn {0} by timeout", txnId)
        )
    
    member internal this.RegisterProducedTopic(topic: CompleteTopicName) =
        producedTopics.GetOrAdd(topic, fun _ ->
            txnOperations.AddPublishPartitionToTxn(txnId, topic))
        
    member internal this.RegisterAckedTopic(topic: CompleteTopicName, subscription: SubscriptionName) =
        ackedTopics.GetOrAdd((topic, subscription), fun _ ->
            txnOperations.AddSubscriptionToTxn(txnId, topic, subscription))
            
    member internal this.RegisterCumulativeAckConsumer(consumerId: ConsumerId, consumerOperations: ConsumerTxnOperations) =
        executeInsideLock (fun () ->
            cumulativeAckConsumers.[consumerId] <- consumerOperations
        ) "Can't ack message cumulatively in closed transaction"
    
    member internal this.RegisterSendOp(sendTask: Task<MessageId>) =
        executeInsideLock (fun () ->
            sendTasks.Add(sendTask)
        ) "Can't send message in closed transaction"
       
    member internal this.RegisterAckOp(ackTask: Task<Unit>) =
        executeInsideLock (fun () ->
            ackTasks.Add(ackTask)
        ) "Can't ack message in closed transaction"
    
    member this.Id = txnId
    
    member private this.AbortInner() =
        task {
            try
                do! allOpComplete()
            with ex ->
                Log.Logger.LogError(ex, "Error during abort txnId={0}", txnId)
            let msgIds =
                sendTasks
                |> Seq.where (fun t -> t.IsCompleted)
                |> Seq.map (fun t -> t.Result)
            let! cumulativeConsumersData =
                cumulativeAckConsumers
                |> Seq.map(fun (KeyValue(_, v)) ->
                    async {
                        let! permits = v.ClearIncomingMessagesAndGetMessageNumber()
                        return (permits, v)
                    })
                |> Async.Parallel
            try
                return! txnOperations.Abort(txnId, msgIds)
            finally
                cumulativeConsumersData
                |> Seq.iter(fun (permits, consumer) -> consumer.IncreaseAvailablePermits(permits))
                cumulativeAckConsumers.Clear()
        }
    
    member private this.CommitInner() =
        task {
            try
                do! allOpComplete()
            with ex ->
                do! this.AbortInner()
                reraize ex
            let msgIds =
                sendTasks |> Seq.map (fun t -> t.Result)
            return! txnOperations.Commit(txnId, msgIds)
        }
    
    member this.Commit() : Task<unit> =
        executeInsideLock (fun () ->
            allowOperations <- false
            this.CommitInner()
        ) "Can't commit a closed transaction"
        
    member this.Abort(): Task<unit> =
        executeInsideLock (fun () ->
            allowOperations <- false
            this.AbortInner()
        ) "Can't abort a closed transaction"
        
    override this.ToString() =
        $"Txn({txnId})"