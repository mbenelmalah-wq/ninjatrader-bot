// ═══════════════════════════════════════════════════════════════
// BelkhayateDashboardStrategyV3.cs — NinjaTrader 8
//
// Trois couches de confirmation :
//   1. Volume Profile → POC (niveau avec le plus de volume)
//   2. Stacked Imbalances → déséquilibre acheteur/vendeur (footprint)
//   3. Delta exhaustion → retournement de pression
//
// Signal BUY  : imbalances haussières OU prix au POC + delta retourne positif
// Signal SELL : imbalances baissières OU prix au POC + delta retourne négatif
//
// Timeframe recommandé : 1 minute — BTC APR26
// ═══════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.Data;
#endregion
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class BelkhayateDashboardStrategyV3 : Strategy
    {
        private const string WEBHOOK_URL   = "https://web-production-365ec.up.railway.app/webhook";
        private const string WEBHOOK_TOKEN = "ninjatrader_secret_2026";

        // ── Paramètres ────────────────────────────────────────────
        [NinjaScriptProperty]
        public string AlpacaSymbol { get; set; } = "BTCUSD";

        [NinjaScriptProperty]
        public int CooldownBars { get; set; } = 3;

        // Stacked Imbalances
        [NinjaScriptProperty]
        public int StackCount { get; set; } = 3;             // niveaux consécutifs déséquilibrés

        [NinjaScriptProperty]
        public double ImbalanceRatio { get; set; } = 3.0;    // ratio ask/bid pour imbalance (300%)

        // Volume Profile
        [NinjaScriptProperty]
        public int POCProximityTicks { get; set; } = 10;     // distance max au POC pour signal

        [NinjaScriptProperty]
        public int ResetVPHour { get; set; } = 0;            // heure UTC reset volume profile (0 = minuit)

        // Delta
        [NinjaScriptProperty]
        public double DeltaThreshold { get; set; } = 1.0;    // seuil delta exhaustion

        [NinjaScriptProperty]
        public bool LogSignals { get; set; } = true;
        // ─────────────────────────────────────────────────────────

        private static readonly HttpClient http = new HttpClient();

        // Volume Profile (session entière)
        private Dictionary<double, double> volByPrice    = new Dictionary<double, double>();
        private Dictionary<double, double> askVolByPrice = new Dictionary<double, double>();
        private Dictionary<double, double> bidVolByPrice = new Dictionary<double, double>();

        // Footprint bougie courante
        private Dictionary<double, double> barAskVol = new Dictionary<double, double>();
        private Dictionary<double, double> barBidVol = new Dictionary<double, double>();

        // Delta
        private double barDelta  = 0;
        private double prevDelta = 0;

        // Résultats de la bougie précédente
        private int    lastBullStack = 0;
        private int    lastBearStack = 0;
        private double lastPOC       = 0;

        // Contrôle signaux
        private int    lastSignalBar = -99;
        private string lastSide      = "";
        private int    signalsSent   = 0;
        private int    lastResetDay  = -1;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name              = "BelkhayateDashboardStrategyV3";
                Calculate         = Calculate.OnEachTick;
                IsOverlay         = true;
                AlpacaSymbol      = "BTCUSD";
                CooldownBars      = 3;
                StackCount        = 3;
                ImbalanceRatio    = 3.0;
                POCProximityTicks = 10;
                ResetVPHour       = 0;
                DeltaThreshold    = 1.0;
                LogSignals        = true;
            }
        }

        // ── Collecte tick par tick ─────────────────────────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;

            double price = e.Price;
            double vol   = e.Volume;

            // Volume profile total
            if (!volByPrice.ContainsKey(price))  volByPrice[price]  = 0;
            volByPrice[price] += vol;

            // Classification acheteur / vendeur
            if (e.Price >= e.Ask)
            {
                barDelta += vol;
                if (!askVolByPrice.ContainsKey(price)) askVolByPrice[price] = 0;
                askVolByPrice[price] += vol;
                if (!barAskVol.ContainsKey(price))     barAskVol[price]     = 0;
                barAskVol[price] += vol;
            }
            else if (e.Price <= e.Bid)
            {
                barDelta -= vol;
                if (!bidVolByPrice.ContainsKey(price)) bidVolByPrice[price] = 0;
                bidVolByPrice[price] += vol;
                if (!barBidVol.ContainsKey(price))     barBidVol[price]     = 0;
                barBidVol[price] += vol;
            }
        }

        // ── Logique principale ─────────────────────────────────────
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < 10) return;

            // Reset volume profile à l'heure configurée
            int today = Time[0].DayOfYear;
            if (today != lastResetDay && Time[0].Hour == ResetVPHour)
            {
                volByPrice.Clear();
                askVolByPrice.Clear();
                bidVolByPrice.Clear();
                lastResetDay = today;
                if (LogSignals) Print($"[V3] Volume Profile réinitialisé — {Time[0]:dd/MM HH:mm}");
            }

            // Analyse de la bougie qui vient de fermer
            if (IsFirstTickOfBar)
            {
                lastBullStack = CountStackedImbalances(barAskVol, barBidVol, true);
                lastBearStack = CountStackedImbalances(barAskVol, barBidVol, false);
                lastPOC       = GetPOC();
                prevDelta     = barDelta;
                barDelta      = 0;
                barAskVol.Clear();
                barBidVol.Clear();

                if (LogSignals)
                    Print($"[V3] Bar {CurrentBar - 1} | POC={lastPOC:F0} | BullStack={lastBullStack} BearStack={lastBearStack} | delta={prevDelta:F1}");
            }

            // Cooldown
            if (CurrentBar - lastSignalBar < CooldownBars) return;

            // ── Conditions ────────────────────────────────────────
            double poc          = lastPOC;
            double proximity    = POCProximityTicks * TickSize;
            bool   nearPOC      = poc > 0 && Math.Abs(Close[0] - poc) <= proximity;
            bool   abovePOC     = nearPOC && Close[0] >= poc;
            bool   belowPOC     = nearPOC && Close[0] <  poc;

            bool   bullImbalance = lastBullStack >= StackCount;
            bool   bearImbalance = lastBearStack >= StackCount;

            bool   deltaReturnBuy  = prevDelta <= -DeltaThreshold && barDelta > 0;
            bool   deltaReturnSell = prevDelta >=  DeltaThreshold && barDelta < 0;

            // Signal = (imbalance OU POC) ET delta retournement
            bool buySignal  = (bullImbalance || abovePOC) && deltaReturnBuy;
            bool sellSignal = (bearImbalance || belowPOC) && deltaReturnSell;

            // ── Flèches ───────────────────────────────────────────
            if (buySignal)
                Draw.ArrowUp(this,   "BUY"  + CurrentBar, true, 0, Low[0]  - TickSize * 3, Brushes.Lime);
            if (sellSignal)
                Draw.ArrowDown(this, "SELL" + CurrentBar, true, 0, High[0] + TickSize * 3, Brushes.Red);

            // ── Envoi webhook ─────────────────────────────────────
            if (buySignal && lastSide != "buy")
            {
                lastSide      = "buy";
                lastSignalBar = CurrentBar;
                signalsSent++;
                string reason = bullImbalance ? "IMBALANCE" : "POC";
                Print($"[V3] ▲ BUY #{signalsSent} [{reason}] | stack={lastBullStack} poc={poc:F0} delta={prevDelta:F1} @ {Close[0]:F2}");
                SendWebhook("buy");
            }
            else if (sellSignal && lastSide != "sell")
            {
                lastSide      = "sell";
                lastSignalBar = CurrentBar;
                signalsSent++;
                string reason = bearImbalance ? "IMBALANCE" : "POC";
                Print($"[V3] ▼ SELL #{signalsSent} [{reason}] | stack={lastBearStack} poc={poc:F0} delta={prevDelta:F1} @ {Close[0]:F2}");
                SendWebhook("sell");
            }
        }

        // ── Calcul stacked imbalances ──────────────────────────────
        private int CountStackedImbalances(
            Dictionary<double, double> askVol,
            Dictionary<double, double> bidVol,
            bool bullish)
        {
            if (askVol.Count == 0 && bidVol.Count == 0) return 0;

            var prices = askVol.Keys.Union(bidVol.Keys).OrderBy(p => p).ToList();
            int maxStack     = 0;
            int currentStack = 0;

            foreach (double price in prices)
            {
                double ask = askVol.ContainsKey(price) ? askVol[price] : 0.001;
                double bid = bidVol.ContainsKey(price) ? bidVol[price] : 0.001;

                bool imbalanced = bullish
                    ? (ask / bid >= ImbalanceRatio)   // acheteurs dominent
                    : (bid / ask >= ImbalanceRatio);  // vendeurs dominent

                if (imbalanced) { currentStack++; maxStack = Math.Max(maxStack, currentStack); }
                else            { currentStack = 0; }
            }
            return maxStack;
        }

        // ── POC = prix avec le plus de volume ─────────────────────
        private double GetPOC()
        {
            if (volByPrice.Count == 0) return 0;
            return volByPrice.OrderByDescending(kv => kv.Value).First().Key;
        }

        // ── Envoi webhook ──────────────────────────────────────────
        private void SendWebhook(string side)
        {
            string payload = "{\"token\":\"" + WEBHOOK_TOKEN
                           + "\",\"symbol\":\"" + AlpacaSymbol
                           + "\",\"side\":\"" + side
                           + "\",\"source\":\"BELKHAYATE\"}";

            Task.Run(async () =>
            {
                try
                {
                    var content  = new StringContent(payload, Encoding.UTF8, "application/json");
                    var response = await http.PostAsync(WEBHOOK_URL, content);
                    string body  = await response.Content.ReadAsStringAsync();
                    Print("[V3][Webhook] " + side.ToUpper() + " " + AlpacaSymbol
                        + " → " + (int)response.StatusCode + " " + body);
                }
                catch (Exception ex)
                {
                    Print("[V3][Webhook] ERREUR : " + ex.Message);
                }
            });
        }
    }
}
