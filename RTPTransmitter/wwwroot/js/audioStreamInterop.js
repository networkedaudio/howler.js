/**
 * audioStreamInterop.js
 *
 * Blazor JS interop module that connects SignalR audio chunks to the
 * HowlerStream plugin for live RTP audio playback.
 *
 * Supports N-channel AES67 streams. The server sends all channels
 * interleaved; this module passes them to HowlerStream which
 * de-interleaves and plays only the channels specified in the
 * channel map. The channel map can be changed live from Blazor.
 */
window.AudioStreamInterop = (function () {
    'use strict';

    var _stream = null;
    var _connection = null;
    var _isConnected = false;
    var _sourceChannels = 1;
    var _channelMap = [0];
    var _stats = {
        chunksReceived: 0,
        chunksPlayed: 0,
        errors: 0
    };

    /**
     * Initialize the audio stream player with Howler.
     * @param {number}  sampleRate      Sample rate (e.g. 48000).
     * @param {number}  sourceChannels  Total channels in the AES67 stream.
     * @param {Array}   channelMap      Array of source channel indices to play.
     *                                  e.g. [0] mono, [2,5] stereo from ch2+ch5.
     * @param {number}  bufferSize      Max chunks to buffer.
     * @param {boolean} debug           Enable debug logging.
     */
    function initialize(sampleRate, sourceChannels, channelMap, bufferSize, debug) {
        if (_stream) {
            _stream.destroy();
        }

        _sourceChannels = sourceChannels || 1;
        _channelMap = channelMap || [0];

        _stream = new HowlerStream({
            sampleRate: sampleRate || 48000,
            sourceChannels: _sourceChannels,
            channelMap: _channelMap,
            bufferSize: bufferSize || 6,
            debug: debug || false,
            volume: 1.0
        });

        _stats = { chunksReceived: 0, chunksPlayed: 0, errors: 0 };

        if (debug) {
            console.log('[AudioStreamInterop] Initialized: sampleRate=' + sampleRate +
                ', sourceChannels=' + _sourceChannels +
                ', channelMap=[' + _channelMap.join(',') + ']' +
                ', bufferSize=' + bufferSize);
        }
    }

    /**
     * Connect to the SignalR audio hub and start receiving chunks.
     * The hub returns stream metadata (including total source channel count)
     * when joining, which is used to auto-configure the stream.
     *
     * @param {string} hubUrl    URL of the SignalR hub (e.g. "/audiohub").
     * @param {string} streamId  Stream group ID to join (e.g. "default").
     */
    function connect(hubUrl, streamId) {
        if (_connection) {
            disconnect();
        }

        _connection = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
            .build();

        // Server sends: ReceiveAudioChunk(base64Data, sourceChannelCount)
        _connection.on('ReceiveAudioChunk', function (base64Data, sourceChannelCount) {
            _stats.chunksReceived++;

            // Update source channel count if the server reports a different value
            if (sourceChannelCount && sourceChannelCount !== _sourceChannels && _stream) {
                _sourceChannels = sourceChannelCount;
                _stream.sourceChannels(sourceChannelCount);
                console.log('[AudioStreamInterop] Source channels updated to ' + sourceChannelCount);
            }

            try {
                processChunk(base64Data);
                _stats.chunksPlayed++;
            } catch (e) {
                _stats.errors++;
                console.error('[AudioStreamInterop] Error processing chunk:', e);
            }
        });

        _connection.onreconnected(function () {
            console.log('[AudioStreamInterop] Reconnected to hub');
            _connection.invoke('JoinAudioStream', streamId || 'default');
        });

        _connection.onclose(function () {
            console.log('[AudioStreamInterop] Connection closed');
            _isConnected = false;
        });

        _connection.start().then(function () {
            _isConnected = true;
            console.log('[AudioStreamInterop] Connected to hub');
            return _connection.invoke('JoinAudioStream', streamId || 'default');
        }).then(function (streamInfo) {
            if (streamInfo) {
                _sourceChannels = streamInfo.sourceChannels || _sourceChannels;
                if (_stream) {
                    _stream.sourceChannels(_sourceChannels);
                }
                console.log('[AudioStreamInterop] Stream info: ' +
                    _sourceChannels + ' channels @ ' +
                    (streamInfo.sampleRate || '?') + 'Hz');
            }
            console.log('[AudioStreamInterop] Joined stream: ' + (streamId || 'default'));
        }).catch(function (err) {
            console.error('[AudioStreamInterop] Connection error:', err);
            _isConnected = false;
        });
    }

    /**
     * Process a base64-encoded interleaved Float32 audio chunk.
     * @param {string} base64Data Base64-encoded Float32Array bytes.
     */
    function processChunk(base64Data) {
        if (!_stream) {
            console.warn('[AudioStreamInterop] Stream not initialized');
            return;
        }

        // Decode base64 to ArrayBuffer
        var binaryString = atob(base64Data);
        var bytes = new Uint8Array(binaryString.length);
        for (var i = 0; i < binaryString.length; i++) {
            bytes[i] = binaryString.charCodeAt(i);
        }

        // Create Float32Array from the raw bytes (interleaved, all channels)
        var float32 = new Float32Array(bytes.buffer);

        // Feed into HowlerStream (de-interleaving happens inside based on channelMap)
        _stream.addChunk(float32);
    }

    /**
     * Change which source channels are played and in what order.
     * Takes effect on the next received chunk.
     *
     * Examples:
     *   setChannelMap([0])         → mono from source channel 0
     *   setChannelMap([3, 7])      → stereo: left=ch3, right=ch7
     *   setChannelMap([4])         → solo source channel 4
     *   setChannelMap([0,1,2,3])   → quad from first 4 channels
     *
     * @param {Array} map Array of zero-based source channel indices.
     */
    function setChannelMap(map) {
        _channelMap = map;
        if (_stream) {
            _stream.setChannelMap(map);
        }
    }

    /**
     * Get the current channel map.
     * @returns {Array}
     */
    function getChannelMap() {
        return _channelMap.slice();
    }

    /**
     * Get the total number of source channels in the stream.
     * @returns {number}
     */
    function getSourceChannels() {
        return _sourceChannels;
    }

    /**
     * Set the playback volume.
     * @param {number} vol Volume from 0.0 to 1.0.
     */
    function setVolume(vol) {
        if (_stream) {
            _stream.volume(vol);
        }
    }

    /**
     * Mute/unmute the stream.
     * @param {boolean} muted True to mute.
     */
    function setMuted(muted) {
        if (_stream) {
            _stream.mute(muted);
        }
    }

    /**
     * Stop playback and clear the buffer.
     */
    function stop() {
        if (_stream) {
            _stream.stop();
        }
    }

    /**
     * Disconnect from SignalR and destroy the stream.
     */
    function disconnect() {
        if (_connection) {
            _connection.stop();
            _connection = null;
            _isConnected = false;
        }
        if (_stream) {
            _stream.destroy();
            _stream = null;
        }
    }

    /**
     * Check if the stream is currently playing.
     * @returns {boolean}
     */
    function isPlaying() {
        return _stream ? _stream.playing() : false;
    }

    /**
     * Check if SignalR is connected.
     * @returns {boolean}
     */
    function isConnected() {
        return _isConnected;
    }

    /**
     * Get playback statistics.
     * @returns {object}
     */
    function getStats() {
        return Object.assign({}, _stats, {
            sourceChannels: _sourceChannels,
            channelMap: _channelMap.slice()
        });
    }

    return {
        initialize: initialize,
        connect: connect,
        addChunk: processChunk,
        setChannelMap: setChannelMap,
        getChannelMap: getChannelMap,
        getSourceChannels: getSourceChannels,
        setVolume: setVolume,
        setMuted: setMuted,
        stop: stop,
        disconnect: disconnect,
        isPlaying: isPlaying,
        isConnected: isConnected,
        getStats: getStats
    };
})();
