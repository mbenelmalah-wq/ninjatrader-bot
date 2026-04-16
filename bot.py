"""
NinjaTrader Bot — Paper Trading Alpaca
Projet séparé — NE PAS MODIFIER les bots SPLV3 et LuxAlgo
Signaux : NinjaTrader → Webhook POST /webhook
"""

import os, json, time, logging, threading
from datetime import datetime, timezone
from dataclasses import dataclass, field
from flask import Flask, request, jsonify
import requests

# ── Config ────────────────────────────────────────────────────────────────────
with open("config.json") as f:
    config = json.load(f)

API_KEY    = config["alpaca"]["api_key"]
API_SECRET = config["alpaca"]["api_secret"]
BASE_URL   = config["alpaca"]["endpoint"]
TOKEN      = config["webhook_token"]

HEADERS = {
    "APCA-API-KEY-ID":     API_KEY,
    "APCA-API-SECRET-KEY": API_SECRET,
    "Content-Type":        "application/json"
}

MAX_POSITIONS = 5
COOLDOWN_MIN  = 0

# ── Logging ───────────────────────────────────────────────────────────────────
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
log = logging.getLogger(__name__)

app = Flask(__name__)

trades_history     = []
cooldown_last      = {}
last_signal        = {}   # {"time", "symbol", "side", "source", "status"}
consecutive_losses = 0
pause_until        = None

# ── State trailing SL ─────────────────────────────────────────────────────────
@dataclass
class TrailSL:
    symbol: str
    entry:  float
    side:   str
    sl:     float
    tp:     float
    mise:   float
    palier: float
    step:   float

active_trails: dict[str, TrailSL] = {}

# ── Cache prix (évite rate limit CoinGecko) ───────────────────────────────────
_prix_cache: dict[str, tuple[float, float]] = {}  # symbol → (price, timestamp)
PRIX_CACHE_TTL = 5   # secondes

# ── Helpers API Alpaca ─────────────────────────────────────────────────────────
def api_call(method, path, payload=None):
    url = BASE_URL + path
    try:
        r = requests.request(method, url, headers=HEADERS, json=payload, timeout=10)
        return r.json()
    except Exception as e:
        return {"error": str(e)}

def get_capital():
    acc = api_call("GET", "/account")
    return float(acc.get("cash", 100000))

def get_equity():
    acc = api_call("GET", "/account")
    return float(acc.get("equity", 100000))

def get_prix(symbol):
    # Cache 30s — évite rate limit CoinGecko
    cached = _prix_cache.get(symbol)
    if cached:
        price, ts = cached
        if time.time() - ts < PRIX_CACHE_TTL:
            log.info(f"  Prix {symbol} depuis cache: ${price}")
            return price

    cg_ids = {"BTC": "bitcoin", "ETH": "ethereum", "SOL": "solana",
              "AVAX": "avalanche-2", "XRP": "ripple", "ADA": "cardano"}
    coin = symbol[:3].upper()
    # Source 1 : CoinGecko (public, sans auth)
    try:
        cg_id = cg_ids.get(coin)
        if cg_id:
            url = f"https://api.coingecko.com/api/v3/simple/price?ids={cg_id}&vs_currencies=usd"
            r = requests.get(url, timeout=8)
            if r.status_code == 200:
                price = r.json().get(cg_id, {}).get("usd")
                if price:
                    _prix_cache[symbol] = (float(price), time.time())
                    log.info(f"  Prix {symbol} via CoinGecko: ${price}")
                    return float(price)
    except Exception as e:
        log.warning(f"CoinGecko échoué: {e}")
    # Source 2 : Binance public (fallback)
    try:
        pair = coin + "USDT"
        url = f"https://api.binance.com/api/v3/ticker/price?symbol={pair}"
        r = requests.get(url, timeout=5)
        if r.status_code == 200:
            price = float(r.json()["price"])
            _prix_cache[symbol] = (price, time.time())
            log.info(f"  Prix {symbol} via Binance: ${price}")
            return price
    except Exception as e:
        log.warning(f"Binance échoué: {e}")
    log.error(f"Impossible d'obtenir le prix pour {symbol}")
    return None

def normalize_symbol(raw):
    raw = raw.upper().replace("/", "").replace("-", "").strip()
    mapping = {
        "BTC": "BTCUSD", "BTCUSDT": "BTCUSD", "XBTUSD": "BTCUSD",
        "ETH": "ETHUSD", "ETHUSDT": "ETHUSD",
        "SOL": "SOLUSD", "SOLUSDT": "SOLUSD",
        "AVAX": "AVAXUSD", "AVAXUSDT": "AVAXUSD",
        "XRP": "XRPUSD",  "XRPUSDT":  "XRPUSD",
    }
    return mapping.get(raw, raw)

def get_ema114(symbol):
    """EMA 114 sur 1min Binance — slope > 0 = haussier, < 0 = baissier, ~0 = aplati."""
    coin = symbol[:3].upper()
    pair = coin + "USDT"
    try:
        url = f"https://api.binance.com/api/v3/klines?symbol={pair}&interval=1m&limit=200"
        r   = requests.get(url, timeout=8)
        if r.status_code != 200:
            return None, None
        closes  = [float(c[4]) for c in r.json()]
        period  = 114
        k       = 2 / (period + 1)
        ema     = sum(closes[:period]) / period
        for p in closes[period:]:
            ema = p * k + ema * (1 - k)
        ema_prev = sum(closes[:period]) / period
        for p in closes[period:-5]:
            ema_prev = p * k + ema_prev * (1 - k)
        slope = (ema - ema_prev) / ema * 100
        return ema, slope
    except Exception as e:
        log.warning(f"EMA114 erreur {symbol}: {e}")
        return None, None

SLOPE_THRESHOLD = 0.003  # % — en dessous = aplati

def is_asian_session():
    cfg = config["sessions"]
    if not cfg.get("block_asian_session", False):
        return False
    now   = datetime.utcnow().strftime("%H:%M")
    start = cfg.get("asian_start", "23:00")
    end   = cfg.get("asian_end",   "08:00")
    if start > end:
        return now >= start or now <= end
    return start <= now <= end

def check_cooldown(symbol):
    last = cooldown_last.get(symbol)
    if last is None:
        return True
    elapsed = (datetime.utcnow() - last).total_seconds() / 60
    if elapsed < COOLDOWN_MIN:
        log.info(f"Cooldown {symbol} — encore {COOLDOWN_MIN - elapsed:.0f} min")
        return False
    return True

def half_kelly(capital, mm):
    wr  = mm.get("default_win_rate", 0.55)
    rr  = mm.get("default_win_loss_ratio", 2.0)
    kelly = max(0, (wr * rr - (1 - wr)) / rr)
    half  = kelly * 0.5
    return round(capital * half, 2)

# ── Chargement historique depuis Alpaca (persistant après redéploiement) ───────
def load_history_from_alpaca():
    try:
        orders = api_call("GET", "/orders?status=closed&limit=50&direction=desc")
        if not isinstance(orders, list):
            return
        for o in orders:
            if o.get("filled_at") and o.get("side") == "buy":
                raw_sym = o.get("symbol", "").replace("/", "")
                if not raw_sym.endswith("USD"):
                    raw_sym = raw_sym + "USD"
                entry = float(o.get("filled_avg_price") or 0)
                trades_history.append({
                    "time":   o.get("filled_at", "")[:16].replace("T", " "),
                    "symbol": raw_sym,
                    "side":   "buy",
                    "source": "ALPACA_HISTORY",
                    "entry":  entry,
                    "sl":     0,
                    "tp":     0,
                    "mise":   float(o.get("filled_qty") or 0) * entry,
                    "exit":   entry,
                    "pnl":    None,
                    "reason": "historique"
                })
        log.info(f"Historique chargé : {len(trades_history)} trades depuis Alpaca")
    except Exception as e:
        log.warning(f"load_history: {e}")

# ── Monitor Trailing SL ────────────────────────────────────────────────────────
def monitor_loop():
    while True:
        try:
            for symbol, t in list(active_trails.items()):
                prix = get_prix(symbol)
                if not prix:
                    continue

                if t.side == "buy":
                    # TP atteint
                    if prix >= t.tp:
                        log.info(f"  TP atteint {symbol} @ {prix:.2f}")
                        api_call("DELETE", f"/positions/{symbol}")
                        pnl = round((prix - t.entry) / t.entry * t.mise, 2)
                        _close_trade(symbol, prix, pnl, "TP")
                        active_trails.pop(symbol, None)
                        continue
                    # SL touché
                    if prix <= t.sl:
                        log.info(f"  SL touché {symbol} @ {prix:.2f}")
                        api_call("DELETE", f"/positions/{symbol}")
                        pnl = round((prix - t.entry) / t.entry * t.mise, 2)
                        _close_trade(symbol, prix, pnl, "SL")
                        active_trails.pop(symbol, None)
                        continue
                    # Trail : montée du SL
                    if prix >= t.palier:
                        nouveau_sl = round(prix - prix * t.step, 2)
                        if nouveau_sl > t.sl:
                            t.sl     = nouveau_sl
                            t.palier = round(prix + prix * t.step, 2)
                            log.info(f"  Trail {symbol} SL→{t.sl:.2f} palier→{t.palier:.2f}")

        except Exception as e:
            log.error(f"Monitor error: {e}")
        time.sleep(10)

def _close_trade(symbol, prix_exit, pnl, reason):
    global consecutive_losses, pause_until
    cooldown_last[symbol] = datetime.utcnow()
    for t in reversed(trades_history):
        if t["symbol"] == symbol and "exit" not in t:
            t["exit"]   = prix_exit
            t["reason"] = reason
            t["pnl"]    = pnl
            break
    # Pause pertes consécutives
    if pnl < 0:
        consecutive_losses += 1
        if consecutive_losses >= 3:
            pause_until = datetime.utcnow().replace(tzinfo=None) + __import__("datetime").timedelta(minutes=30)
            log.warning(f"3 pertes consécutives — pause jusqu'à {pause_until.strftime('%H:%M')} UTC")
    else:
        consecutive_losses = 0

# ── Webhook ────────────────────────────────────────────────────────────────────
@app.route("/webhook", methods=["POST"])
def webhook():
    try:
        data = request.get_json(force=True)
        log.info(f"Webhook recu : {data}")

        if data.get("token") != TOKEN:
            log.warning("Token invalide")
            return jsonify({"error": "unauthorized"}), 401

        symbol = normalize_symbol(data.get("symbol", "BTCUSD"))
        side   = data.get("side", "").lower()
        source = data.get("source", "NINJATRADER").upper()

        if side not in ("buy", "sell"):
            return jsonify({"error": "side invalide"}), 400

        # Enregistrement dernier signal reçu
        last_signal.update({
            "time":   datetime.utcnow().strftime("%H:%M:%S"),
            "symbol": symbol,
            "side":   side,
            "source": source,
            "status": "reçu"
        })

        # Filtre session asiatique
        if is_asian_session():
            log.info("Session asiatique — signal ignoré")
            return jsonify({"status": "asian_session_blocked"})

        # Filtre EMA 114 Belkhayate — bloque BUY si tendance baissière ou aplatie
        if side == "buy":
            ema114, slope = get_ema114(symbol)
            if ema114 is not None:
                if abs(slope) < SLOPE_THRESHOLD:
                    log.info(f"  EMA114 aplatie ({slope:.4f}%) — BUY bloqué")
                    return jsonify({"status": "ema114_flat", "slope": slope})
                if slope < 0:
                    log.info(f"  EMA114 baissière ({slope:.4f}%) — BUY bloqué")
                    return jsonify({"status": "ema114_bearish", "slope": slope})

        # Pause pertes consécutives
        if pause_until and datetime.utcnow() < pause_until:
            reste = int((pause_until - datetime.utcnow()).total_seconds() / 60)
            log.info(f"Pause pertes — encore {reste} min")
            return jsonify({"status": "pause_pertes", "minutes_restantes": reste})

        # SELL — ferme la position si elle existe (signal Belkhayate baissier)
        if side == "sell":
            if symbol not in active_trails:
                log.info(f"SELL {symbol} — aucune position ouverte, ignoré")
                return jsonify({"status": "no_position_to_close"})
            trail = active_trails[symbol]
            prix  = get_prix(symbol) or trail.entry
            api_call("DELETE", f"/positions/{symbol}")
            pnl = round((prix - trail.entry) / trail.entry * trail.mise, 2)
            _close_trade(symbol, prix, pnl, "SIGNAL_SELL")
            active_trails.pop(symbol, None)
            log.info(f"SELL {symbol} exécuté @ {prix:.2f} | PnL={pnl:+.2f}$")
            return jsonify({"status": "position_closed", "symbol": symbol, "pnl": pnl})

        # BUY
        if not check_cooldown(symbol):
            return jsonify({"status": "cooldown"})

        # Max positions
        pos_list = api_call("GET", "/positions")
        if isinstance(pos_list, list) and len(pos_list) >= MAX_POSITIONS:
            log.info(f"Max positions atteint — signal ignoré")
            return jsonify({"status": "max_positions"})

        # Déjà en position BUY
        if symbol in active_trails:
            log.info(f"Déjà en position {symbol} — ignoré")
            return jsonify({"status": "already_in_position"})

        # Capital + MM
        capital = get_capital()
        sym_key = f"money_management_{symbol.upper()}"
        mm = config.get(sym_key, config["money_management_default"])
        mise = half_kelly(capital, mm)

        if mise < 10:
            log.warning(f"Mise trop faible ({mise}) — ignoré")
            return jsonify({"status": "insufficient_capital"})

        # Prix + calcul SL/TP
        prix = get_prix(symbol)
        if not prix:
            return jsonify({"error": "prix indisponible"}), 500

        sl_pct   = mm["trailing_sl_pct"]
        tp_pct   = mm["take_profit_pct"]
        trig_pct = mm["profit_trigger_pct"]
        step_pct = mm["trail_step_pct"]

        sl     = round(prix * (1 - sl_pct), 2)
        tp     = round(prix * (1 + tp_pct), 2)
        palier = round(prix * (1 + trig_pct), 2)

        # Calcul quantité
        qty = round(mise / prix, 6)
        if qty <= 0:
            return jsonify({"error": "qty invalide"}), 500

        # Ordre Alpaca
        order = api_call("POST", "/orders", {
            "symbol":        f"{symbol[:3]}/USD",
            "qty":           str(qty),
            "side":          "buy",
            "type":          "market",
            "time_in_force": "gtc"
        })

        if "error" in order or order.get("status") == "rejected":
            log.error(f"Ordre rejeté : {order}")
            return jsonify({"error": "ordre rejeté", "detail": order}), 500

        # Enregistrement trail
        active_trails[symbol] = TrailSL(
            symbol=symbol, entry=prix, side="buy",
            sl=sl, tp=tp, mise=mise, palier=palier, step=step_pct
        )
        cooldown_last[symbol] = datetime.utcnow()

        # Historique
        trades_history.append({
            "time":   datetime.utcnow().isoformat(),
            "symbol": symbol,
            "side":   "buy",
            "source": source,
            "entry":  prix,
            "sl":     sl,
            "tp":     tp,
            "mise":   mise
        })

        last_signal["status"] = "exécuté ✅"
        log.info(f"  BUY {symbol} qty={qty} @ {prix:.2f} | SL={sl:.2f} TP={tp:.2f} | mise=${mise:.2f}")
        return jsonify({"status": "executed", "symbol": symbol, "qty": qty,
                        "entry": prix, "sl": sl, "tp": tp})

    except Exception as e:
        log.error(f"Webhook error: {e}")
        return jsonify({"error": str(e)}), 500

# ── Recover trails manquants ──────────────────────────────────────────────────
@app.route("/recover")
def recover():
    """Reconstruit les trails manquants depuis les positions Alpaca ouvertes."""
    try:
        pos_list = api_call("GET", "/positions")
        if not isinstance(pos_list, list):
            return jsonify({"error": "impossible de lire les positions"}), 500
        recovered = []
        for p in pos_list:
            sym   = p["symbol"].replace("/", "")
            if sym in active_trails:
                continue
            entry  = float(p["avg_entry_price"])
            sym_key = f"money_management_{sym.upper()}"
            mm = config.get(sym_key, config["money_management_default"])
            sl_pct   = mm["trailing_sl_pct"]
            tp_pct   = mm["take_profit_pct"]
            trig_pct = mm["profit_trigger_pct"]
            step_pct = mm["trail_step_pct"]
            mise     = abs(float(p["market_value"]))
            active_trails[sym] = TrailSL(
                symbol=sym, entry=entry, side="buy",
                sl=round(entry * (1 - sl_pct), 2),
                tp=round(entry * (1 + tp_pct), 2),
                mise=mise,
                palier=round(entry * (1 + trig_pct), 2),
                step=step_pct
            )
            recovered.append({"symbol": sym, "entry": entry,
                               "sl": active_trails[sym].sl,
                               "tp": active_trails[sym].tp})
            log.info(f"  RECOVER {sym} entry={entry:.2f} SL={active_trails[sym].sl:.2f} TP={active_trails[sym].tp:.2f}")
        return jsonify({"status": "ok", "recovered": recovered})
    except Exception as e:
        return jsonify({"error": str(e)}), 500

# ── Status JSON ───────────────────────────────────────────────────────────────
@app.route("/status")
def status():
    return jsonify({
        "status":              "running",
        "time":                datetime.utcnow().isoformat(),
        "capital":             get_capital(),
        "equity":              get_equity(),
        "positions":           list(active_trails.keys()),
        "trails":              {s: {"entry": t.entry, "sl": t.sl, "tp": t.tp} for s, t in active_trails.items()},
        "consecutive_losses":  consecutive_losses,
        "pause_pertes":        pause_until.isoformat() if pause_until else None,
        "cooldowns":           {s: t.isoformat() for s, t in cooldown_last.items()},
    })

# ── Dashboard ──────────────────────────────────────────────────────────────────
@app.route("/")
@app.route("/dashboard")
def dashboard():
    eq        = get_equity()
    cap       = get_capital()
    trades    = trades_history
    wins      = [t for t in trades if (t.get("pnl") or 0) > 0]
    total_pnl = sum(t.get("pnl") or 0 for t in trades)
    wr        = round(len(wins) / len(trades) * 100, 1) if trades else 0
    pnl_color = "#00e676" if total_pnl >= 0 else "#ff5252"
    eq_color  = "#00e676" if eq >= 100000 else "#ff5252"
    asian     = is_asian_session()

    # Dernier signal
    sig = last_signal
    if sig:
        sig_side_color = "#00e676" if sig.get("side") == "buy" else "#ff5252"
        sig_arrow      = "▲ BUY" if sig.get("side") == "buy" else "▼ SELL"
        sig_html = f"""
        <div class="card" style="border-color:#f0883e44;margin-bottom:14px">
          <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
            <span style="color:#8b949e;font-size:.75rem">DERNIER SIGNAL BELKHAYATE</span>
            <span style="color:#8b949e;font-size:.75rem">{sig.get('time','')} UTC</span>
          </div>
          <div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:8px;text-align:center">
            <div>
              <div style="color:#8b949e;font-size:.7rem">Direction</div>
              <div style="color:{sig_side_color};font-size:1.1rem;font-weight:700">{sig_arrow}</div>
            </div>
            <div>
              <div style="color:#8b949e;font-size:.7rem">Symbole</div>
              <div style="font-size:1.1rem;font-weight:700">{sig.get('symbol','')}</div>
            </div>
            <div>
              <div style="color:#8b949e;font-size:.7rem">Statut</div>
              <div style="color:#f0883e;font-size:.9rem;font-weight:600">{sig.get('status','')}</div>
            </div>
          </div>
        </div>"""
    else:
        sig_html = '<div class="card" style="text-align:center;color:#8b949e;padding:16px;margin-bottom:14px">En attente du premier signal Belkhayate...</div>'

    # Positions ouvertes
    positions_html = ""
    for sym, t in active_trails.items():
        prix_now = get_prix(sym) or t.entry
        pnl_live = round((prix_now - t.entry) / t.entry * 100, 3)
        pnl_usd  = round((prix_now - t.entry) / t.entry * t.mise, 2)
        pc = "#00e676" if pnl_live >= 0 else "#ff5252"
        positions_html += f"""
        <div class="card pos-card">
            <div class="pos-sym">▲ {sym} <span style="font-size:.75rem;color:#8b949e">BUY</span></div>
            <div class="pos-grid">
                <span>Entrée</span><span>${t.entry:,.2f}</span>
                <span>Prix actuel</span><span>${prix_now:,.2f}</span>
                <span>SL</span><span style="color:#ff5252">${t.sl:,.2f}</span>
                <span>TP</span><span style="color:#00e676">${t.tp:,.2f}</span>
                <span>P&L live</span><span style="color:{pc}">{'+' if pnl_live>=0 else ''}{pnl_live}% ({'+$' if pnl_usd>=0 else '-$'}{abs(pnl_usd):.2f})</span>
            </div>
        </div>"""

    # Historique — chaque ordre affiché individuellement (BUY vert / SELL rouge)
    # Pas d'appariement : un SELL peut fermer plusieurs BUY simultanément
    orders_raw = api_call("GET", "/orders?status=closed&limit=200&direction=desc")
    orders     = orders_raw if isinstance(orders_raw, list) else []
    filled     = [o for o in orders if o.get("status") == "filled"]
    # Tri décroissant (plus récent en premier) — déjà desc depuis Alpaca
    filled_sorted = sorted(filled, key=lambda o: o.get("filled_at", ""), reverse=True)

    trades_html = ""
    for o in filled_sorted[:40]:
        sym    = o.get("symbol", "").replace("/", "")
        side   = o.get("side", "")
        prix   = float(o.get("filled_avg_price") or 0)
        qty    = float(o.get("filled_qty") or 0)
        t_str  = o.get("filled_at", "")[:16].replace("T", " ")
        heure  = t_str[11:16]
        is_buy = side == "buy"
        sc     = "#00e676" if is_buy else "#ff5252"
        arrow  = "▲ BUY"  if is_buy else "▼ SELL"
        # Montant cash : négatif pour BUY (dépense), positif pour SELL (recette)
        cash   = prix * qty
        cash_c = "#ff5252" if is_buy else "#00e676"
        cash_s = f'-${cash:,.2f}' if is_buy else f'+${cash:,.2f}'
        trades_html += f"""
        <tr>
            <td><small>{heure}</small></td>
            <td>{sym}</td>
            <td style="color:{sc}">{arrow}</td>
            <td>${prix:,.1f}</td>
            <td>{qty:.4f}</td>
            <td style="color:{cash_c}">{cash_s}</td>
        </tr>"""

    session_banner = f'<div style="background:#ff525211;border:1px solid #ff525233;border-radius:8px;padding:8px;text-align:center;color:#ff5252;font-size:.8rem;margin-bottom:12px">⏸ Session asiatique active — signaux bloqués jusqu\'à 08:00 UTC</div>' if asian else ""

    return f"""<!DOCTYPE html>
<html lang="fr">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Belkhayate Bot</title>
<meta http-equiv="refresh" content="15">
<style>
  * {{ box-sizing:border-box; margin:0; padding:0 }}
  body {{ background:#0d1117; color:#e6edf3; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif; padding:12px }}
  h1 {{ font-size:1.2rem; color:#f0883e; margin-bottom:14px; text-align:center }}
  .badge {{ display:inline-block; background:#f0883e22; color:#f0883e; border:1px solid #f0883e55; border-radius:20px; padding:2px 10px; font-size:.75rem; margin-left:8px }}
  .grid {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(140px,1fr)); gap:10px; margin-bottom:14px }}
  .card {{ background:#161b22; border:1px solid #30363d; border-radius:10px; padding:14px }}
  .card .label {{ color:#8b949e; font-size:.75rem; margin-bottom:4px }}
  .card .value {{ font-size:1.3rem; font-weight:700 }}
  .pos-card {{ margin-bottom:10px }}
  .pos-sym {{ font-size:1rem; font-weight:700; color:#f0883e; margin-bottom:8px }}
  .pos-grid {{ display:grid; grid-template-columns:1fr 1fr; gap:4px 12px; font-size:.85rem }}
  .pos-grid span:nth-child(odd) {{ color:#8b949e }}
  h2 {{ font-size:.95rem; color:#8b949e; margin:14px 0 8px; border-bottom:1px solid #21262d; padding-bottom:6px }}
  table {{ width:100%; border-collapse:collapse; font-size:.8rem }}
  th {{ color:#8b949e; text-align:left; padding:6px 4px; border-bottom:1px solid #21262d }}
  td {{ padding:6px 4px; border-bottom:1px solid #21262d11 }}
  .dot {{ display:inline-block; width:8px; height:8px; border-radius:50%; background:#f0883e; margin-right:6px; animation:pulse 2s infinite }}
  @keyframes pulse {{ 0%,100%{{opacity:1}} 50%{{opacity:.4}} }}
</style>
</head>
<body>
<h1><span class="dot"></span>Belkhayate Orderflow Bot <span class="badge">PAPER</span></h1>

{session_banner}

{sig_html}

<div class="grid">
  <div class="card">
    <div class="label">Equity</div>
    <div class="value" style="color:{eq_color}">${eq:,.0f}</div>
  </div>
  <div class="card">
    <div class="label">Capital libre</div>
    <div class="value">${cap:,.0f}</div>
  </div>
  <div class="card">
    <div class="label">P&L total</div>
    <div class="value" style="color:{pnl_color}">{'+' if total_pnl>=0 else ''}${total_pnl:,.2f}</div>
  </div>
  <div class="card">
    <div class="label">Win Rate</div>
    <div class="value" style="color:{'#00e676' if wr>=50 else '#ff9800'}">{wr}%</div>
  </div>
  <div class="card">
    <div class="label">Trades</div>
    <div class="value">{len(trades)}</div>
  </div>
  <div class="card">
    <div class="label">Positions</div>
    <div class="value" style="color:#f0883e">{len(active_trails)}</div>
  </div>
</div>

{'<h2>Positions ouvertes</h2>' + positions_html if active_trails else '<div class="card" style="text-align:center;color:#8b949e;padding:20px">Aucune position ouverte</div>'}

<h2>Derniers trades ({len(trades)})</h2>
<div style="overflow-x:auto">
<table>
  <tr><th>Heure</th><th>Sym</th><th>Side</th><th>Prix</th><th>Qty</th><th>Montant</th></tr>
  {trades_html if trades_html else '<tr><td colspan="6" style="text-align:center;color:#8b949e;padding:20px">En attente des signaux Belkhayate...</td></tr>'}
</table>
</div>
<p style="text-align:center;color:#8b949e;font-size:.7rem;margin-top:12px">Refresh auto 15s — Belkhayate Orderflow → BTCUSD Alpaca</p>
</body></html>"""

# ── Start ──────────────────────────────────────────────────────────────────────
if __name__ == "__main__":
    load_history_from_alpaca()
    t = threading.Thread(target=monitor_loop, daemon=True)
    t.start()
    port = int(os.environ.get("PORT", 8080))
    log.info(f"NinjaTrader Bot démarré — port {port}")
    log.info(f"Webhook : POST /webhook (token: {TOKEN})")
    app.run(host="0.0.0.0", port=port)
