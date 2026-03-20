/*!
 *  howler.js v2.2.4 Stream Plugin
 *  howlerjs.com
 *
 *  (c) 2013-2020, James Simpson of GoldFire Studios
 *  goldfirestudios.com
 *
 *  MIT License
 */

(function() {

  'use strict';

  /** HowlerStream Plugin **/
  /***************************************************************************/

  /**
   * SoundBuffer - manages scheduling and playback of streamed PCM audio chunks
   * using the Web Audio API. Handles N-channel AES67 de-interleaving and lets
   * callers select which source channels to play via a channel map.
   *
   * @param {AudioContext} ctx              The Web Audio AudioContext.
   * @param {Number}       sampleRate       Sample rate of the incoming audio (e.g. 48000).
   * @param {Number}       sourceChannels   Total channels in the incoming interleaved data.
   * @param {Array}        channelMap       Array mapping output channels to source channels.
   *                                        e.g. [0] = mono from source ch 0
   *                                             [2, 5] = stereo: left=source 2, right=source 5
   *                                             [0, 1, 2, 3] = quad from source channels 0-3
   * @param {Number}       bufferSize       Max number of chunks to buffer before discarding.
   * @param {Boolean}      debug            Enable debug logging.
   */
  var SoundBuffer = function(ctx, sampleRate, sourceChannels, channelMap, bufferSize, debug) {
    this.ctx = ctx;
    this.sampleRate = sampleRate || 48000;
    this.sourceChannels = sourceChannels || 1;
    this.channelMap = channelMap || [0];
    this.bufferSize = bufferSize || 6;
    this.debug = debug || false;
    this.chunks = [];
    this.isPlaying = false;
    this.startTime = 0;
    this.lastChunkOffset = 0;
    this.gainNode = null;
    this._volume = 1;
    this._muted = false;
  };

  SoundBuffer.prototype = {
    /**
     * Initialize the gain node for volume control.
     */
    init: function() {
      var self = this;
      if (!self.gainNode) {
        self.gainNode = (typeof self.ctx.createGain === 'undefined')
          ? self.ctx.createGainNode()
          : self.ctx.createGain();
        self.gainNode.gain.setValueAtTime(self._volume, self.ctx.currentTime);
        self.gainNode.connect(Howler.masterGain || self.ctx.destination);
      }
      return self;
    },

    /**
     * De-interleave an N-channel interleaved Float32Array and extract only the
     * channels specified in the channel map, returning per-output-channel arrays.
     *
     * Input layout: [ch0_f0, ch1_f0, ..., chN_f0, ch0_f1, ch1_f1, ...]
     *
     * @param  {Float32Array} interleaved  The interleaved source samples.
     * @return {Array}                     Array of Float32Arrays, one per output channel.
     */
    deInterleave: function(interleaved) {
      var self = this;
      var srcCh = self.sourceChannels;
      var map = self.channelMap;
      var outCh = map.length;
      var frames = Math.floor(interleaved.length / srcCh);
      var result = [];

      for (var c = 0; c < outCh; c++) {
        result.push(new Float32Array(frames));
      }

      for (var f = 0; f < frames; f++) {
        var srcBase = f * srcCh;
        for (var c = 0; c < outCh; c++) {
          var srcIdx = map[c];
          result[c][f] = (srcIdx >= 0 && srcIdx < srcCh)
            ? interleaved[srcBase + srcIdx]
            : 0;
        }
      }

      return result;
    },

    /**
     * Create an AudioBufferSourceNode from interleaved PCM data.
     * De-interleaves using the channel map, creates a Web Audio buffer
     * with the correct number of output channels, and fills each channel.
     *
     * @param  {Float32Array} interleaved  Interleaved source samples (all source channels).
     * @return {AudioBufferSourceNode}
     */
    createChunk: function(interleaved) {
      var self = this;
      var channelData = self.deInterleave(interleaved);
      var outChannels = channelData.length;
      var framesPerChannel = channelData[0].length;

      var audioBuffer = self.ctx.createBuffer(outChannels, framesPerChannel, self.sampleRate);
      for (var c = 0; c < outChannels; c++) {
        audioBuffer.getChannelData(c).set(channelData[c]);
      }

      var source = self.ctx.createBufferSource();
      source.buffer = audioBuffer;
      self.init();
      source.connect(self.gainNode);

      source.onended = function() {
        var idx = self.chunks.indexOf(source);
        if (idx !== -1) {
          self.chunks.splice(idx, 1);
        }
        if (self.chunks.length === 0) {
          self.isPlaying = false;
          self.startTime = 0;
          self.lastChunkOffset = 0;
        }
      };

      return source;
    },

    /**
     * Log a debug message.
     * @param {String} data Message to log.
     */
    log: function(data) {
      if (this.debug) {
        console.log(new Date().toUTCString() + ' [HowlerStream] ' + data);
      }
    },

    /**
     * Add a chunk of interleaved PCM audio data to the buffer for playback.
     * The chunk is de-interleaved according to the current channel map.
     * @param {Float32Array} data Interleaved PCM audio samples (all source channels).
     */
    addChunk: function(data) {
      var self = this;

      if (self.isPlaying && self.chunks.length > self.bufferSize) {
        self.log('chunk discarded (buffer full: ' + self.chunks.length + ')');
        return;
      } else if (self.isPlaying && self.chunks.length <= self.bufferSize) {
        self.log('chunk accepted (' + self.chunks.length + ' in buffer)');
        var chunk = self.createChunk(data);
        chunk.start(self.startTime + self.lastChunkOffset);
        self.lastChunkOffset += chunk.buffer.duration;
        self.chunks.push(chunk);
      } else if (self.chunks.length < (self.bufferSize / 2) && !self.isPlaying) {
        self.log('chunk queued (' + self.chunks.length + ' in buffer)');
        var chunk = self.createChunk(data);
        self.chunks.push(chunk);
      } else {
        self.log('queued chunks scheduled (' + self.chunks.length + ' chunks)');
        self.isPlaying = true;
        var chunk = self.createChunk(data);
        self.chunks.push(chunk);
        self.startTime = self.ctx.currentTime;
        self.lastChunkOffset = 0;
        for (var i = 0; i < self.chunks.length; i++) {
          var c = self.chunks[i];
          c.start(self.startTime + self.lastChunkOffset);
          self.lastChunkOffset += c.buffer.duration;
        }
      }
    },

    /**
     * Set or get the volume.
     * @param  {Number} vol Volume from 0.0 to 1.0.
     * @return {SoundBuffer/Number}
     */
    volume: function(vol) {
      var self = this;
      if (typeof vol === 'number') {
        self._volume = vol;
        if (self.gainNode) {
          self.gainNode.gain.setValueAtTime(self._muted ? 0 : vol, self.ctx.currentTime);
        }
        return self;
      }
      return self._volume;
    },

    /**
     * Mute or unmute the stream.
     * @param  {Boolean} muted True to mute.
     * @return {SoundBuffer}
     */
    mute: function(muted) {
      var self = this;
      self._muted = muted;
      if (self.gainNode) {
        self.gainNode.gain.setValueAtTime(muted ? 0 : self._volume, self.ctx.currentTime);
      }
      return self;
    },

    /**
     * Stop all playing chunks and clear the buffer.
     */
    stop: function() {
      var self = this;
      for (var i = 0; i < self.chunks.length; i++) {
        try {
          self.chunks[i].stop(0);
        } catch (e) {
          // Ignore errors from chunks that haven't started.
        }
      }
      self.chunks = [];
      self.isPlaying = false;
      self.startTime = 0;
      self.lastChunkOffset = 0;
      return self;
    },

    /**
     * Destroy the stream buffer and release resources.
     */
    destroy: function() {
      var self = this;
      self.stop();
      if (self.gainNode) {
        self.gainNode.disconnect();
        self.gainNode = null;
      }
    }
  };

  /**
   * HowlerStream - high-level streaming interface that integrates with Howler's
   * global AudioContext and master gain. Create a stream, then feed it interleaved
   * PCM chunks. Supports N-channel AES67 sources with per-client channel selection.
   *
   * Usage:
   *   // Listen to a 64-channel AES67 stream, outputting source channels 3 and 7 as stereo:
   *   var stream = new HowlerStream({
   *     sampleRate: 48000,
   *     sourceChannels: 64,
   *     channelMap: [3, 7]     // left=ch3, right=ch7
   *   });
   *   stream.addChunk(interleavedFloat32Array);
   *
   *   // Switch to monitoring channel 12 as mono:
   *   stream.setChannelMap([12]);
   *
   *   // Mix channels 0-3 into stereo (0+2 left, 1+3 right):
   *   // (use custom mixing via addChunkMixed for advanced scenarios)
   *
   * @param {Object} options Configuration options.
   */
  var HowlerStream = function(options) {
    var self = this;
    options = options || {};

    // Ensure the AudioContext is set up.
    if (!Howler.ctx) {
      Howler._setup();
    }

    // Resume the audio context if it's suspended.
    if (Howler.ctx && Howler.ctx.state === 'suspended') {
      Howler.ctx.resume();
    }

    self._sampleRate = options.sampleRate || 48000;
    self._sourceChannels = options.sourceChannels || 1;
    self._channelMap = options.channelMap || self._buildDefaultMap(self._sourceChannels);
    self._bufferSize = options.bufferSize || 6;
    self._debug = options.debug || false;
    self._volume = options.volume !== undefined ? options.volume : 1;

    self._buffer = new SoundBuffer(
      Howler.ctx,
      self._sampleRate,
      self._sourceChannels,
      self._channelMap,
      self._bufferSize,
      self._debug
    );

    self._buffer.volume(self._volume);
  };

  HowlerStream.prototype = {
    /**
     * Build a default channel map. For 1-2 source channels, map straight through.
     * For >2, default to the first two channels as stereo.
     * @param  {Number} sourceChannels Total source channels.
     * @return {Array}  Default channel map.
     */
    _buildDefaultMap: function(sourceChannels) {
      if (sourceChannels <= 2) {
        var map = [];
        for (var i = 0; i < sourceChannels; i++) {
          map.push(i);
        }
        return map;
      }
      // For multi-channel, default to first stereo pair
      return [0, 1];
    },

    /**
     * Add a chunk of interleaved PCM Float32 audio data to the stream.
     * The data contains all source channels interleaved; the current
     * channel map controls which channels are played.
     *
     * @param {Float32Array} data Interleaved PCM samples for all source channels.
     * @return {HowlerStream}
     */
    addChunk: function(data) {
      var self = this;

      // Resume the context if needed (e.g. after user gesture).
      if (Howler.ctx && Howler.ctx.state === 'suspended') {
        Howler.ctx.resume();
      }

      self._buffer.addChunk(data);
      return self;
    },

    /**
     * Get or set the source channel count. When set, also updates the
     * internal buffer's source channel count.
     * @param  {Number} count Total source channels in the interleaved data.
     * @return {HowlerStream/Number}
     */
    sourceChannels: function(count) {
      var self = this;
      if (typeof count === 'number' && count > 0) {
        self._sourceChannels = count;
        self._buffer.sourceChannels = count;
        return self;
      }
      return self._sourceChannels;
    },

    /**
     * Get or set the channel map. The channel map is an array where each
     * element is a zero-based source channel index. The position in the
     * array determines the output channel:
     *
     *   [5]       → mono output from source channel 5
     *   [3, 7]    → stereo: left=source 3, right=source 7
     *   [0,1,2,3] → quad output from source channels 0,1,2,3
     *
     * Changing the map takes effect on the next chunk received (the
     * current buffer is flushed to avoid glitches from mixed mappings).
     *
     * @param  {Array} map Array of source channel indices.
     * @return {HowlerStream/Array}
     */
    channelMap: function(map) {
      var self = this;
      if (Array.isArray(map) && map.length > 0) {
        self._channelMap = map;
        // Flush the current buffer so chunks with mixed mappings don't overlap.
        self._buffer.stop();
        self._buffer.channelMap = map;
        return self;
      }
      return self._channelMap.slice();
    },

    /**
     * Convenience alias for channelMap().
     * @param  {Array} map Array of source channel indices.
     * @return {HowlerStream/Array}
     */
    setChannelMap: function(map) {
      return this.channelMap(map);
    },

    /**
     * Get/set volume.
     * @param  {Number} vol Volume from 0.0 to 1.0.
     * @return {HowlerStream/Number}
     */
    volume: function(vol) {
      var self = this;
      if (typeof vol === 'number') {
        self._volume = vol;
        self._buffer.volume(vol);
        return self;
      }
      return self._volume;
    },

    /**
     * Mute/unmute the stream.
     * @param  {Boolean} muted True to mute.
     * @return {HowlerStream}
     */
    mute: function(muted) {
      this._buffer.mute(muted);
      return this;
    },

    /**
     * Check if the stream buffer is currently playing audio.
     * @return {Boolean}
     */
    playing: function() {
      return this._buffer.isPlaying;
    },

    /**
     * Stop the stream and clear all buffered chunks.
     * @return {HowlerStream}
     */
    stop: function() {
      this._buffer.stop();
      return this;
    },

    /**
     * Destroy the stream and release all resources.
     */
    destroy: function() {
      this._buffer.destroy();
      this._buffer = null;
    }
  };

  // Export HowlerStream.
  if (typeof window !== 'undefined') {
    window.HowlerStream = HowlerStream;
  }

  if (typeof exports !== 'undefined') {
    exports.HowlerStream = HowlerStream;
  }

  if (typeof define === 'function' && define.amd) {
    define([], function() {
      return { HowlerStream: HowlerStream };
    });
  }

  if (typeof global !== 'undefined') {
    global.HowlerStream = HowlerStream;
  }

})();
