// Gibt die Rohdaten vom Mikrofon an die Seite weiter, der Rest passiert dort
class ZwijgCapture extends AudioWorkletProcessor {
  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (channel) this.port.postMessage(channel.slice(0));
    return true;
  }
}

registerProcessor("zwijg-capture", ZwijgCapture);
