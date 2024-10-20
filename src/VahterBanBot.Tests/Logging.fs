module VahterBanBot.Tests.Logging

open System
open Microsoft.Extensions.Logging

type StringLogger() =
    member _.Messages = ResizeArray<string>() 
    interface ILogger with
        member this.BeginScope(state) = { new IDisposable with member _.Dispose() = () }
        member this.IsEnabled(logLevel) = true
        member this.Log(logLevel, _eventId, state, ex, formatter) =
            this.Messages.Add($"{logLevel} {formatter.Invoke(state, ex)}")
            
