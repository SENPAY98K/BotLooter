﻿using System.Net;
using BotLooter.Resources;
using BotLooter.Steam;
using BotLooter.Steam.Contracts;
using BotLooter.Steam.Contracts.Responses;
using Polly;
using Polly.Retry;
using RestSharp;
using Serilog;

namespace BotLooter.Looting;

public class LootClient
{
    public SteamAccountCredentials Credentials { get; }
    
    private readonly SteamUserSession _steamSession;
    private readonly SteamWeb _steamWeb;

    private readonly AsyncRetryPolicy<bool> _sendTradeOfferPolicy;

    public LootClient(SteamAccountCredentials credentials, SteamUserSession steamSession, SteamWeb steamWeb)
    {
        Credentials = credentials;
        _steamSession = steamSession;
        _steamWeb = steamWeb;

        _sendTradeOfferPolicy = Policy
            .HandleResult<bool>(x => x is false)
            .WaitAndRetryAsync(3, _ => TimeSpan.FromSeconds(10));
    }

    public async Task<(int? LootedItemCount, string Message)> TryLoot(TradeOfferUrl tradeOfferUrl, Configuration configuration)
    {
        var (isSession, ensureSessionMessage) = await _steamSession.TryEnsureSession();

        if (!isSession)
        {
            return (null, ensureSessionMessage);
        }
        
        Log.Logger.Debug("{Login} : {SessionType}", Credentials.Login, ensureSessionMessage);

        var (assets, getAssetsMessage) = await GetAssetsToSend(configuration);

        if (assets is null)
        {
            return (null, getAssetsMessage);
        }

        if (assets.Count < 1)
        {
            return (null, "Пустые инвентари");
        }

        var tradeOffer = new JsonTradeOffer
        {
            NewVersion = true,
            Version = 4
        };

        foreach (var inventoryAsset in assets)
        {
            if (!int.TryParse(inventoryAsset.Amount, out var amount))
            {
                amount = 1;
            }
            
            var asset = new TradeOfferAsset
            {
                AppId = $"{inventoryAsset.Appid}",
                ContextId = $"{inventoryAsset.Contextid}",
                Amount = amount,
                AssetId = inventoryAsset.Assetid
            };

            tradeOffer.Me.Assets.Add(asset);
        }

        var (tradeOfferId, sendTradeOfferMessage) = await SendTradeOffer(tradeOfferUrl, tradeOffer);

        if (tradeOfferId is null)
        {
            return (null, sendTradeOfferMessage);
        }

        var confirmationResult = await _steamSession.AcceptConfirmation(tradeOfferId.Value);

        if (!confirmationResult)
        {
            return (null, "Не смог подтвердить обмен");
        }

        return (tradeOffer.Me.Assets.Count, $"Залутан! Предметов: {tradeOffer.Me.Assets.Count}");
    }

    private async Task<(List<Asset>? Assets, string message)> GetAssetsToSend(Configuration configuration)
    {
        var filteredOut = new HashSet<string>();
        
        var assets = new List<Asset>();

        var index = 0;
        
        foreach (var inventory in configuration.Inventories)
        {
            var split = inventory.Split('/');

            if (split.Length != 2)
            {
                continue;
            }

            var inventoryId = split[0];
            var contextId = split[1];
            
            var inventoryResponse = await _steamWeb.LoadInventory(inventoryId, contextId);
            
            if (inventoryResponse is not {} inventoryData)
            {
                return (null, $"Не смог получить инвентарь {inventory}.");
            }
            
            foreach (var description in inventoryData.Descriptions.Where(d => !d.Tradable))
            {
                filteredOut.Add(description.Classid);
            }

            if (configuration.IgnoreNotMarketable)
            {
                foreach (var description in inventoryData.Descriptions.Where(d => !d.Marketable))
                {
                    filteredOut.Add(description.Classid);
                }
            }

            if (configuration.IgnoreMarketable)
            {
                foreach (var description in inventoryData.Descriptions.Where(d => d.Marketable))
                {
                    filteredOut.Add(description.Classid);
                }
            }

            var notFilteredOutAssets = inventoryData.Assets.Where(a => !filteredOut.Contains(a.Classid));
            
            assets.AddRange(notFilteredOutAssets);

            var isLast = index == configuration.Inventories.Count - 1;
            
            if (!isLast)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            
            index++;
        }

        return (assets, "");
    }

    private async Task<(ulong? TradeOfferId, string Message)> SendTradeOffer(TradeOfferUrl tradeOfferUrl, JsonTradeOffer tradeOffer)
    {
        var sendTradeOfferResponse = new RestResponse() as RestResponse<SendTradeOfferResponse>;
        
        await _sendTradeOfferPolicy.ExecuteAsync(async () =>
        {
            sendTradeOfferResponse = await _steamWeb.SendTradeOffer(tradeOfferUrl, tradeOffer);

            if (sendTradeOfferResponse.StatusCode != HttpStatusCode.OK) {
                return false;
            }

            return true;
        });

        if (sendTradeOfferResponse.Data is not { } sendTradeOfferData)
        {
            return (null, $"Не смог отправить обмен - {sendTradeOfferResponse.StatusCode} {sendTradeOfferResponse.Content}");
        }

        if (!string.IsNullOrWhiteSpace(sendTradeOfferData.Error))
        {
            if (sendTradeOfferData.Error.Contains("Steam Guard enabled for at least 15 days"))
            {
                var cantTradeTime = await _steamWeb.GetHelpWhyCantITradeTime();

                return (null, $"Обмен будет доступен через {cantTradeTime}");
            }
            
            return (null, $"Не смог отправить обмен - {sendTradeOfferData.Error}");
        }

        if (!ulong.TryParse(sendTradeOfferData.TradeofferId, out var tradeOfferId))
        {
            return (null, $"Не смог спарсить айди обмена - {sendTradeOfferResponse.StatusCode} {sendTradeOfferResponse.Content}");
        }

        return (tradeOfferId, "");
    }
}