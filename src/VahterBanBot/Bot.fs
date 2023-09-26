﻿module VahterBanBot.Bot

open System
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open VahterBanBot.Types
open VahterBanBot.Utils

let isChannelMessage (message: Message) =
    message.From.IsBot &&
    message.From.FirstName = "Channel" &&
    message.From.Username = "Channel_Bot"
    
let isPingCommand (message: Message) =
    message.Text = "/ban ping"

let isBanOnReplyMessage (message: Message) =
    message.Text = "/ban" &&
    message.ReplyToMessage <> null
    
let isMessageFromAdmin (botConfig: BotConfiguration) (message: Message) =
    botConfig.AllowedUsers.Contains message.From.Id

let isBannedPersonAdmin (botConfig: BotConfiguration) (message: Message) =
    botConfig.AllowedUsers.Contains message.ReplyToMessage.From.Id
    
let isBanAuthorized (botConfig: BotConfiguration) (message: Message) (logger: ILogger) =
    let fromUserId = message.From.Id
    let fromUsername = message.From.Username
    let targetUserId = message.ReplyToMessage.From.Id
    let targetUsername = message.ReplyToMessage.From.Username
    
    // check that user is allowed to ban others
    if isMessageFromAdmin botConfig message then
        // check that user is not trying to ban other admins
        if isBannedPersonAdmin botConfig message then
            logger.LogWarning $"User {fromUsername} {fromUserId} tried to ban admin {targetUsername} {targetUserId}"
            false
        else
            true
    else
        false
    
let banInAllChats (botConfig: BotConfiguration) (botClient: ITelegramBotClient) targetUserId = task {
    let banTasks =
        botConfig.ChatsToMonitor
        |> Seq.map (fun chatId -> task {
            // ban user in each chat
            try
                do! botClient.BanChatMemberAsync (ChatId chatId, targetUserId, DateTime.UtcNow.AddMonths 13)
                return Ok chatId
            with e ->
                return Error (chatId, e)
        })
    return! Task.WhenAll banTasks
}

let aggregateBanResultInLogMsg (logger: ILogger) (message: Message) (banResults: Result<int64, int64 * exn> []) =
    let targetUserId = message.ReplyToMessage.From.Id
    let targetUsername = message.ReplyToMessage.From.Username
    let logMsgBuilder = StringBuilder()
    %logMsgBuilder.AppendLine($"Result of ban {targetUsername} ({targetUserId}) in chats:")

    (logMsgBuilder, banResults)
    ||> Array.fold (fun (sb: StringBuilder) result ->
        match result with
        | Ok chatId ->
            sb.AppendLine($"{chatId} - OK")
        | Error (chatId, e) ->
            logger.LogError($"Failed to ban user {targetUsername} ({targetUserId}) in chat {chatId}", e)
            sb.AppendLine($"{chatId} - FAILED. {e.Message}")
    )
    |> string

let onUpdate
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (message: Message) = task {

    if isNull message || isNull message.From then
        logger.LogWarning "Received update without message"
        
    // check if message comes from channel, we should delete it immediately
    elif botConfig.ShouldDeleteChannelMessages && isChannelMessage message then
        
        do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
        let probablyChannelName =
            if message.SenderChat <> null then
                message.SenderChat.Title
            else
                "[unknown]"
        logger.LogInformation $"Deleted message from channel {probablyChannelName}"

    // check if message is a known command
    // and check that user is allowed to ban others
    elif isBanOnReplyMessage message && isBanAuthorized botConfig message logger then

        // try ban user in all monitored chats
        let! banResults = banInAllChats botConfig botClient message.ReplyToMessage.From.Id
        
        // produce aggregated log message
        let logMsg = aggregateBanResultInLogMsg logger message banResults
            
        // log both to logger and to logs channel
        let! _ = botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg)
        logger.LogInformation logMsg
        
    // ping command for testing that bot works and you can talk to it
    elif isPingCommand message && isMessageFromAdmin botConfig message then
        let! _ = botClient.SendTextMessageAsync(ChatId(message.Chat.Id), "pong")
        ()
}
