// ═══════════════════════════════════════════════════════════════
// BelkhayateDashboardStrategyV3.cs — NinjaTrader 8
//
// Fusion V2 + Volume Profile POC
//   - Signaux V2 (breakout / reversal / vacuum / momentum) → flèches
//   - POC calculé en temps réel → zone de confirmation
//   - Signal renforcé si prix proche du POC
//   - Calculate.OnBarClose (stable, compatible tous feeds)
//
// Timeframe : 1 minute — BTC APR26
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

        [NinjaScriptProperty]
        public double DeltaSlopeThreshold { get; set; } = 0.2;   // seuil signal V2

        [NinjaScriptProperty]
        public double IcebergVolMult { get; set; } = 1.5;         // multiplicateur volume iceberg

        [NinjaScriptProperty]
        public double VacuumEnergyMin { get; set; } = 2.0;        // énergie min vacuum

        [NinjaScriptProperty]
        public int POCProximityTicks { get; set; } = 15;          // distance max au POC

        [NinjaScriptProperty]
        public int ResetVPHour { get; set; } = 0;                 // heure UTC reset volume profile

        [NinjaScriptProperty]
        public bool RequirePOC { get; set; } = false;             // false = POC optionnel, true = obligatoire

        [NinjaScriptProperty]
        public bool LogSignals { get; set; } = true;
        // ─────────────────────────────────────────────────────────

        private static readonly HttpClient http = new HttpClient();

        // Indicateurs V2
        private EMA emaFast;
        private EMA emaSlow;
        private ATR atr;
        private SMA volMA;

        private Series<double> delta;
        private Series<double> deltaSlope;
        private Series<double> deltaAbsSeries;

        // Volume Profile
        private Dictionary<double, double> volByPrice = new Dictionary<double, double>();
        private int lastResetDay = -1;

        // Contrôle signaux
        private int    lastSignalBar = -99;
        private string lastSide      = "";
        private int    signalsSent   = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                 = "BelkhayateDashboardStrategyV3";
                Calculate            = Calculate.OnBarClose;
                IsOverlay            = true;
                AlpacaSymbol         = "BTCUSD";
                CooldownBars         = 3;
                DeltaSlopeThreshold  = 0.2;
                IcebergVolMult       = 1.5;
                VacuumEnergyMin      = 2.0;
                POCProximityTicks    = 15;
                ResetVPHour          = 0;
                RequirePOC           = false;
                LogSignals           = true;
            }
            else if (State == State.DataLoaded)
            {
                emaFast = EMA(50);
                emaSlow = EMA(100);
                atr     = ATR(14);
                volMA   = SMA(Volume, 20);

                delta          = new Series<double>(this);
                deltaSlope     = new Series<double>(this);
                deltaAbsSeries = new Series<double>(this);
            }
        }

        // ── Volume Profile (tick par tick) ─────────────────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;

            // Reset volume profile à l'heure configurée
            int today = e.Time.DayOfYear;
            if (today != lastResetDay && e.Time.Hour == ResetVPHour)
            {
                volByPrice.Clear();
                lastResetDay = today;
                if (LogSignals) Print($"[V3] Volume Profile reset — {e.Time:dd/MM HH:mm}");
            }

            double price = e.Price;
            if (!volByPrice.ContainsKey(price)) volByPrice[price] = 0;
            volByPrice[price] += e.Volume;
        }

        // ── Logique principale (OnBarClose) ────────────────────────
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 100) return;

            // ── Indicateurs V2 ────────────────────────────────────
            double gravity  = (emaFast[0] + emaSlow[0]) / 2;
            double range    = High[0] - Low[0];
            double energy   = atr[0] > 0 ? range / atr[0] : 0;
            double volNorm  = Volume[0] / volMA[0];

            delta[0]           = (Close[0] - Open[0]) * Volume[0];
            double deltaFast   = EMA(delta, 5)[0];
            deltaAbsSeries[0]  = Math.Abs(delta[0]);
            double deltaAbs    = EMA(deltaAbsSeries, 20)[0];
            deltaSlope[0]      = deltaAbs > 0 ? deltaFast / deltaAbs : 0;

            bool trendUp     = Close[0] > emaSlow[0];
            bool trendDown   = Close[0] < emaSlow[0];
            bool expansion   = energy > 1.0;
            bool compression = energy < 0.8;

            bool breakoutBuy  = Close[0] > MAX(High, 15)[1];
            bool breakoutSell = Close[0] < MIN(Low, 15)[1];

            bool iceberg     = Volume[0] > volMA[0] * IcebergVolMult
                            && Math.Abs(Close[0] - Open[0]) < atr[0] * 0.3;
            bool icebergBuy  = iceberg && Close[0] > gravity;
            bool icebergSell = iceberg && Close[0] < gravity;

            bool vacuumBuy  = energy > VacuumEnergyMin && volNorm > 1.5 && Close[0] > gravity;
            bool vacuumSell = energy > VacuumEnergyMin && volNorm > 1.5 && Close[0] < gravity;

            bool breakoutSignalBuy  = breakoutBuy  && expansion   && deltaSlope[0] >  DeltaSlopeThreshold;
            bool breakoutSignalSell = breakoutSell && expansion   && deltaSlope[0] < -DeltaSlopeThreshold;
            bool reversalBuy        = icebergBuy   && compression && deltaSlope[0] >  0;
            bool reversalSell       = icebergSell  && compression && deltaSlope[0] <  0;
            bool vacuumSignalBuy    = vacuumBuy    && expansion;
            bool vacuumSignalSell   = vacuumSell   && expansion;
            bool momentumBuy        = deltaSlope[0] >  DeltaSlopeThreshold && Close[0] > gravity;
            bool momentumSell       = deltaSlope[0] < -DeltaSlopeThreshold && Close[0] < gravity;

            bool v2Buy  = (breakoutSignalBuy  || reversalBuy  || vacuumSignalBuy  || momentumBuy)  && trendUp;
            bool v2Sell = (breakoutSignalSell || reversalSell || vacuumSignalSell || momentumSell) && trendDown;

            // ── Volume Profile POC ────────────────────────────────
            double poc        = GetPOC();
            double proximity  = POCProximityTicks * TickSize;
            bool   nearPOC    = poc > 0 && Math.Abs(Close[0] - poc) <= proximity;
            bool   pocBuy     = nearPOC && Close[0] >= poc;
            bool   pocSell    = nearPOC && Close[0] <  poc;

            // ── Signal final ──────────────────────────────────────
            bool buySignal;
            bool sellSignal;

            if (RequirePOC)
            {
                // Mode strict : signal V2 ET proche du POC
                buySignal  = v2Buy  && pocBuy;
                sellSignal = v2Sell && pocSell;
            }
            else
            {
                // Mode normal : signal V2 (POC renforce mais pas obligatoire)
                buySignal  = v2Buy;
                sellSignal = v2Sell;
            }

            // ── Flèches ───────────────────────────────────────────
            if (v2Buy)
            {
                Brush color = nearPOC ? Brushes.Cyan : Brushes.Lime;  // cyan = signal POC renforcé
                Draw.ArrowUp(this, "BUY" + CurrentBar, true, 0, Low[0] - TickSize * 3, color);
            }
            if (v2Sell)
            {
                Brush color = nearPOC ? Brushes.Yellow : Brushes.Red;
                Draw.ArrowDown(this, "SELL" + CurrentBar, true, 0, High[0] + TickSize * 3, color);
            }

            // ── Log ───────────────────────────────────────────────
            if (LogSignals && (v2Buy || v2Sell))
            {
                string type = breakoutSignalBuy || breakoutSignalSell ? "BREAKOUT"
                            : reversalBuy       || reversalSell       ? "REVERSAL"
                            : vacuumSignalBuy   || vacuumSignalSell   ? "VACUUM"
                            : "MOMENTUM";
                Print($"[V3] {(v2Buy ? "▲ BUY" : "▼ SELL")} [{type}] pocProx={nearPOC} poc={poc:F0} delta={deltaSlope[0]:F3} @ {Close[0]:F2}");
            }

            // ── Cooldown ──────────────────────────────────────────
            if (CurrentBar - lastSignalBar < CooldownBars) return;

            // ── Envoi webhook ─────────────────────────────────────
            if (buySignal && lastSide != "buy")
            {
                lastSide      = "buy";
                lastSignalBar = CurrentBar;
                signalsSent++;
                Print($"[V3] ▲ BUY #{signalsSent} envoyé | poc={nearPOC} @ {Close[0]:F2}");
                SendWebhook("buy");
            }
            else if (sellSignal && lastSide != "sell")
            {
                lastSide      = "sell";
                lastSignalBar = CurrentBar;
                signalsSent++;
                Print($"[V3] ▼ SELL #{signalsSent} envoyé | poc={nearPOC} @ {Close[0]:F2}");
                SendWebhook("sell");
            }
        }

        // ── POC = prix avec le plus de volume ─────────────────────
        private double GetPOC()
        {
            if (volByPrice.Count == 0) return 0;
            return volByPrice.OrderByDescending(kv => kv.Value).First().Key;
        }

        // ── Webhook ────────────────────────────────────────────────
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
