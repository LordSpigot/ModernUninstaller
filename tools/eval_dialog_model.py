"""Leave-one-out-Auswertung des Dialog-Assistenten (Python-Nachbau des C#-Modells).
Aufruf:  python tools/eval_dialog_model.py
Prüft, wie gut das Modell Dialoge bedient, die es beim Training nicht gesehen hat.
Nach Änderungen an seed-dialogs.json oder lexicon.json ausführen."""
import json, re, sys, math, random
DIM = 1 << 18
import os
ML = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src', 'ModernUninstaller', 'Services', 'Ml')
LEX = json.load(open(os.path.join(ML, 'lexicon.json'), encoding='utf-8'))
# Wort -> Konzepte (Präfixvergleich ab len>=4 bei Lexikonwort mit '*')
def build(lex):
    exact, pref = {}, []
    for concept, words in lex.items():
        for w in words:
            if w.endswith('*'): pref.append((w[:-1], concept))
            else: exact.setdefault(w, []).append(concept)
    return exact, pref
LBL_EX, LBL_PF = build(LEX['label'])
CTX_EX, CTX_PF = build(LEX['context'])

def concepts(tokens, ex, pf):
    out = []
    for t in tokens:
        out += ex.get(t, [])
        for p, c in pf:
            if t.startswith(p): out.append(c)
    return sorted(set(out))

def fnv(s):
    h = 2166136261
    for ch in s:
        h ^= ord(ch) & 0xFFFF
        h = (h * 16777619) & 0xFFFFFFFF
    return h

def norm(s):
    s = s.lower().replace('&', '')
    s = re.sub(r'\.\.\.|…|[<>]', ' ', s)
    s = re.sub(r'[^\w\s\'-]', ' ', s)
    return re.sub(r'\s+', ' ', s).strip()

def toks(s): return re.findall(r'[^\W_]+', s.lower().replace('&', ''))
def stem(t): return t[:5]

def ctxinfo(w):
    tk = toks(w['title'] + ' ' + ' '.join(w['texts']))
    st = []
    for t in tk:
        s = stem(t)
        if s not in st: st.append(s)
    return st[:60], concepts(tk, CTX_EX, CTX_PF)

def control_feats(w, i, ctx):
    stems, cc = ctx
    c = w['controls'][i]
    typ, label = c[0], c[1]
    st = '-' if typ == 'button' else ('on' if len(c) > 2 and c[2] else 'off')
    ts = typ + st
    l = norm(label); lt = toks(l)
    lc = concepts(lt, LBL_EX, LBL_PF)
    f = ['bias:' + ts, 'l:' + l]
    for t in lt: f += ['w:' + ts + ':' + t]
    g = '^' + l.replace(' ', '_') + '$'
    for k in range(len(g) - 2): f.append('g:' + g[k:k+3])
    for a in lc:
        f.append('lc:' + ts + ':' + a)
        for b in cc: f.append('lx:' + ts + ':' + a + '|' + b)
    if not lc: f.append('lc:' + ts + ':none')
    for b in cc: f.append('cc:' + ts + '|' + b)
    for t in lt[:4]:
        for s in stems: f.append('x:' + ts + ':' + stem(t) + '|' + s)
    if typ == 'button':
        btn = [j for j, cc2 in enumerate(w['controls']) if cc2[0] == 'button']
        n = len(btn)
        if n == 1: f.append('p:only')
        elif i == btn[-1]: f.append('p:last')
        elif i == btn[0]: f.append('p:first')
    return f

def wait_feats(w, ctx):
    stems, cc = ctx
    f = ['wait']
    for b in cc: f.append('wc:' + b)
    for s in stems: f.append('ws:' + s)
    btn = [c for c in w['controls'] if c[0] == 'button']
    lcs = set()
    for c in btn: lcs.update(concepts(toks(norm(c[1])), LBL_EX, LBL_PF))
    for a in sorted(lcs): f.append('wl:' + a)       # welche Button-Arten gibt es überhaupt
    f.append('wn:' + str(min(len(btn), 4)))
    return f

H = lambda fs: [fnv(x) % DIM for x in fs]

def is_toggle(c): return c[0] in ('checkbox', 'radio')
def toggle_cands(w): return [i for i, c in enumerate(w['controls']) if is_toggle(c) and not (c[0] == 'radio' and len(c) > 2 and c[2])]
def button_cands(w): return [i for i, c in enumerate(w['controls']) if c[0] == 'button']

def sig(z): return 1 / (1 + math.exp(-max(-30, min(30, z))))

class Model:
    def __init__(s): s.w = [0.0] * DIM
    def dot(s, fs): return sum(s.w[k] for k in fs)
    def train(s, samples, epochs=30, lr=0.3, l2=1e-6, seed=7):
        tog, btn = [], []
        for w, wt in samples:
            ctx = ctxinfo(w); t = w['target']
            for i in toggle_cands(w): tog.append((H(control_feats(w, i, ctx)), 1.0 if i == t else 0.0, wt))
            if t == -1 or w['controls'][t][0] == 'button':
                cands = [H(control_feats(w, i, ctx)) for i in button_cands(w)] + [H(wait_feats(w, ctx))]
                y = button_cands(w).index(t) if t >= 0 else len(cands) - 1
                btn.append((cands, y, wt))
        rnd = random.Random(seed)
        items = [('t', x) for x in tog] + [('b', x) for x in btn]
        for ep in range(epochs):
            rnd.shuffle(items)
            a = lr / (1 + ep * 0.15)
            for kind, x in items:
                if kind == 't':
                    fs, y, wt = x; g = (sig(s.dot(fs)) - y) * wt
                    for k in fs: s.w[k] -= a * g
                else:
                    cands, y, wt = x
                    zs = [s.dot(fs) for fs in cands]; m = max(zs)
                    es = [math.exp(z - m) for z in zs]; Z = sum(es)
                    for j, fs in enumerate(cands):
                        g = (es[j] / Z - (1.0 if j == y else 0.0)) * wt
                        for k in fs: s.w[k] -= a * g
        return s

    def decide(s, w, hi=0.7, margin=0.3, toglo=0.35):
        ctx = ctxinfo(w)
        tp = sorted(((sig(s.dot(H(control_feats(w, i, ctx)))), i) for i in toggle_cands(w)), reverse=True)
        if tp and tp[0][0] >= hi: return ('click', tp[0][1], tp[0][0])
        if tp and tp[0][0] >= toglo: return ('ask', tp[0][1], tp[0][0])
        bc = button_cands(w)
        zs = [s.dot(H(control_feats(w, i, ctx))) for i in bc] + [s.dot(H(wait_feats(w, ctx)))]
        m = max(zs); es = [math.exp(z - m) for z in zs]; Z = sum(es)
        ps = sorted(((e / Z, (bc + [-1])[j]) for j, e in enumerate(es)), reverse=True)
        p1, i1 = ps[0]; p2 = ps[1][0] if len(ps) > 1 else 0
        if i1 == -1: return ('wait', -1, p1)
        if p1 >= hi and p1 - p2 >= margin: return ('click', i1, p1)
        return ('ask', i1, p1)

RISKY = {'install', 'upgrade', 'repair', 'modify', 'send'}
def lc(c): return set(concepts(toks(norm(c[1])), LBL_EX, LBL_PF))
def is_on(c): return len(c) > 2 and bool(c[2])
def reboot_now(c): s = lc(c); return 'restart' in s and 'later' not in s and 'no' not in s
def safety(w, d, i):
    if d != 'click': return d, None
    c = w['controls'][i]; s = lc(c); _, cc = ctxinfo(w)
    if c[0] == 'checkbox' and not is_on(c): return 'ask', 'würde ankreuzen'
    if s & RISKY and not (c[0] == 'checkbox' and is_on(c)): return 'ask', 'riskant: ' + ','.join(s & RISKY)
    if reboot_now(c): return 'ask', 'Neustart'
    if c[0] == 'button':
        if any(x[0] != 'button' and is_on(x) and reboot_now(x) for x in w['controls']): return 'ask', 'Neustart gewählt'
        if 'yes' in s and ('restart' in cc or 'cancel_q' in cc): return 'ask', 'Ja bei Neustart/Abbruch'
        if 'no' in s and 'q_remove' in cc and not (set(cc) & {'shared','keep','restart','cancel_q','survey','browser','offer','repair'}): return 'ask', 'Nein bei Deinstallationsfrage'
    return d, None

# Fehlerfenster-Regel aus SafetyRules.cs
def safety_full(w, d, i):
    _, cc = ctxinfo(w)
    if 'error' in cc and d != 'ask' and w['controls']: return 'ask', 'Fehlermeldung'
    return safety(w, d, i)

if __name__ == '__main__':
    S = json.load(open(os.path.join(ML, 'seed-dialogs.json'), encoding='utf-8'))['samples']
    ok = ask = wrong = 0
    for k, w in enumerate(S):
        m = Model().train([(x, 1.0) for j, x in enumerate(S) if j != k])
        d, i, p = m.decide(w); t = w['target']
        d, why = safety_full(w, d, i)
        lab = lambda j: w['controls'][j][1] if j >= 0 else 'warten'
        if (d == 'wait' and t == -1) or (d == 'click' and i == t): ok += 1
        elif d == 'ask': ask += 1
        else:
            wrong += 1; print('FALSCH', k, w['title'][:30], '|', (w['texts'] or [''])[0][:45], '| tat', d, lab(i), '| erwartet', lab(t))
    print(f'Unbekannte Dialoge: richtig {ok}, Rückfrage {ask}, falsch {wrong} von {len(S)}')
