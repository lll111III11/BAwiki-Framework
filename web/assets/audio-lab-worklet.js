const FRAME_MS = 16;

class BawlProbe extends AudioWorkletProcessor {
  constructor() {
    super();
    this.n = Math.max(128, Math.round((sampleRate * FRAME_MS) / 1000));
    this.aLow = 1 - Math.exp((-2 * Math.PI * 250) / sampleRate);
    this.aMid = 1 - Math.exp((-2 * Math.PI * 2500) / sampleRate);
    this.l250L = 0; this.l250R = 0; this.l2500L = 0; this.l2500R = 0;
    this.prevV = 0;
    this.prevPeak = 1e-4;
    this.lastRms = 0;
    this.idx = 0;
    this.reset();
  }

  reset() {
    this.i = 0; this.cnt = 0;
    this.peak = 0; this.sum = 0; this.zc = 0; this.prevSign = 0;
    this.dsum = 0; this.dn = 0;
    this.low = 0; this.mid = 0; this.hi = 0;
    this.side = 0; this.mid2 = 0; this.lr = 0; this.l2 = 0; this.r2 = 0;
    this.hot = 0;
  }

  process(inputs, outputs) {
    const inp = inputs[0] || [];
    const out = outputs[0] || [];
    const ch = inp.length;
    if (!ch) return true;
    const L = inp[0];
    const R = ch > 1 ? inp[1] : inp[0];

    for (let c = 0; c < out.length; c++) {
      const src = inp[c] || L;
      if (out[c].length === src.length) out[c].set(src);
    }
    const n = L.length;
    const aLow = this.aLow, aMid = this.aMid;
    for (let k = 0; k < n; k++) {
      const a = L[k], b = R[k];
      const v = (a + b) * 0.5;
      const av = v < 0 ? -v : v;
      if (av > this.peak) this.peak = av;
      if (av >= 0.995) this.hot++;
      this.sum += v * v;
      const sign = v < 0 ? -1 : 1;
      if (this.prevSign && sign !== this.prevSign) this.zc++;
      this.prevSign = sign;
      const d = v - this.prevV;
      this.prevV = v;
      this.dsum += d * d;
      this.dn++;
      this.l250L += aLow * (a - this.l250L);
      this.l250R += aLow * (b - this.l250R);
      this.l2500L += aMid * (a - this.l2500L);
      this.l2500R += aMid * (b - this.l2500R);
      const lo = (this.l250L + this.l250R) * 0.5;
      const md = (this.l2500L + this.l2500R) * 0.5;
      this.low += lo * lo;
      this.mid += (md - lo) * (md - lo);
      this.hi += (v - md) * (v - md);
      const sd = (a - b) * 0.5;
      this.side += sd * sd;
      this.mid2 += v * v;
      this.lr += a * b;
      this.l2 += a * a;
      this.r2 += b * b;
      this.cnt++;
      if (++this.i >= this.n) this.flush();
    }
    return true;
  }

  flush() {
    const cnt = Math.max(1, this.cnt);
    const rms = Math.sqrt(this.sum / cnt);
    const f = new Float32Array(12);
    f[0] = this.peak;
    f[1] = rms;
    f[2] = this.zc / cnt;
    f[3] = Math.abs(rms - this.lastRms);
    this.lastRms = rms;
    f[4] = Math.min(1, (Math.sqrt(this.dsum / Math.max(1, this.dn)) / Math.max(1e-4, rms)) * 0.25);
    f[5] = Math.sqrt(this.low / cnt);
    f[6] = Math.sqrt(this.mid / cnt);
    f[7] = Math.sqrt(this.hi / cnt);
    const sideR = Math.sqrt(this.side / cnt);
    const midR = Math.sqrt(this.mid2 / cnt);
    f[8] = Math.min(2, sideR / Math.max(1e-4, midR));
    f[9] = this.lr / Math.max(1e-9, Math.sqrt(this.l2 * this.r2));
    f[10] = Math.min(8, this.peak / Math.max(1e-4, this.prevPeak));
    this.prevPeak = Math.max(this.peak, 1e-4);
    f[11] = this.hot / cnt;
    this.port.postMessage({ i: this.idx++, f: f }, [f.buffer]);
    this.reset();
  }
}

registerProcessor('bawl-probe', BawlProbe);
