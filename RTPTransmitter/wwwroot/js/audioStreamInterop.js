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
    var _debug = false;
    var _debugChunkInterval = 100;
    var _statsLogTimer = null;
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

        _debug = !!debug;
        _stats = { chunksReceived: 0, chunksPlayed: 0, errors: 0 };

        console.log('[AudioStreamInterop] Initialized: sampleRate=' + sampleRate +
            ', sourceChannels=' + _sourceChannels +
            ', channelMap=[' + _channelMap.join(',') + ']' +
            ', bufferSize=' + bufferSize +
            ', debug=' + _debug);
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
            console.log('[AudioStreamInterop] Disconnecting previous connection before reconnect');
            disconnect();
        }

        console.log('[AudioStreamInterop] Building SignalR connection to ' + hubUrl + ' for stream "' + (streamId || 'default') + '"');

        var builder = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect([0, 1000, 2000, 5000, 10000]);

        if (_debug) {
            builder.configureLogging(signalR.LogLevel.Information);
        }

        _connection = builder.build();

        console.log('[AudioStreamInterop] Registering ReceiveAudioChunk handler');

        // Server sends: ReceiveAudioChunk(base64Data, sourceChannelCount)
        _connection.on('ReceiveAudioChunk', function (base64Data, sourceChannelCount) {
            _stats.chunksReceived++;

            // Log the very first chunk in detail, then every Nth chunk
            if (_stats.chunksReceived === 1) {
                console.log('[AudioStreamInterop] *** First chunk received ***' +
                    ' | base64 length=' + (base64Data ? base64Data.length : 'null') +
                    ' | sourceChannelCount=' + sourceChannelCount +
                    ' | _sourceChannels=' + _sourceChannels +
                    ' | _stream=' + (!!_stream));
            } else if (_debug && (_stats.chunksReceived % _debugChunkInterval === 0)) {
                console.log('[AudioStreamInterop] Chunk #' + _stats.chunksReceived +
                    ' | base64 len=' + (base64Data ? base64Data.length : 'null') +
                    ' | played=' + _stats.chunksPlayed +
                    ' | errors=' + _stats.errors);
            }

            // Update source channel count if the server reports a different value
            if (sourceChannelCount && sourceChannelCount !== _sourceChannels && _stream) {
                console.log('[AudioStreamInterop] Source channels changed: ' + _sourceChannels + ' -> ' + sourceChannelCount);
                _sourceChannels = sourceChannelCount;
                _stream.sourceChannels(sourceChannelCount);
            }

            try {
                processChunk(base64Data);
                _stats.chunksPlayed++;
            } catch (e) {
                _stats.errors++;
                console.error('[AudioStreamInterop] Error processing chunk #' + _stats.chunksReceived + ':', e);
                if (_stats.errors <= 5) {
                    console.error('[AudioStreamInterop] Chunk data debug:' +
                        ' base64 length=' + (base64Data ? base64Data.length : 'null') +
                        ' sourceChannelCount=' + sourceChannelCount +
                        ' streamInitialized=' + (!!_stream));
                }
            }
        });

        _connection.onreconnecting(function (err) {
            console.warn('[AudioStreamInterop] Connection reconnecting...', err || '');
            _isConnected = false;
        });

        _connection.onreconnected(function (connectionId) {
            console.log('[AudioStreamInterop] Reconnected (id=' + connectionId + '), rejoining stream');
            _isConnected = true;
            _connection.invoke('JoinAudioStream', streamId || 'default');
        });

        _connection.onclose(function (err) {
            console.log('[AudioStreamInterop] Connection closed', err || '');
            _isConnected = false;
            stopStatsLogger();
        });

        console.log('[AudioStreamInterop] Starting connection...');

        _connection.start().then(function () {
            _isConnected = true;
            console.log('[AudioStreamInterop] Connected to hub (state=' + _connection.state + ')');
            console.log('[AudioStreamInterop] Invoking JoinAudioStream("' + (streamId || 'default') + '")...');
            return _connection.invoke('JoinAudioStream', streamId || 'default');
        }).then(function (streamInfo) {
            console.log('[AudioStreamInterop] JoinAudioStream response:', JSON.stringify(streamInfo));
            if (streamInfo) {
                _sourceChannels = streamInfo.sourceChannels || _sourceChannels;
                if (_stream) {
                    _stream.sourceChannels(_sourceChannels);
                }
                console.log('[AudioStreamInterop] Stream joined: ' +
                    _sourceChannels + ' channels @ ' +
                    (streamInfo.sampleRate || '?') + 'Hz' +
                    ', streamId="' + (streamInfo.streamId || streamId || 'default') + '"');
            } else {
                console.warn('[AudioStreamInterop] JoinAudioStream returned null/undefined');
            }
            console.log('[AudioStreamInterop] Waiting for ReceiveAudioChunk messages...');
            startStatsLogger();
        }).catch(function (err) {
            console.error('[AudioStreamInterop] Connection/join error:', err);
            _isConnected = false;
        });
    }

    function startStatsLogger() {
        stopStatsLogger();
        _statsLogTimer = setInterval(function () {
            console.log('[AudioStreamInterop] Stats: received=' + _stats.chunksReceived +
                ' played=' + _stats.chunksPlayed +
                ' errors=' + _stats.errors +
                ' connected=' + _isConnected +
                ' streamOk=' + (!!_stream));
        }, 5000);
    }

    function stopStatsLogger() {
        if (_statsLogTimer) {
            clearInterval(_statsLogTimer);
            _statsLogTimer = null;
        }
    }

    /**
     * Process a base64-encoded interleaved Float32 audio chunk.
     * @param {string} base64Data Base64-encoded Float32Array bytes.
     */
    function processChunk(base64Data) {
        if (!_stream) {
            console.warn('[AudioStreamInterop] processChunk called but stream not initialized — dropping chunk');
            return;
        }

        if (!base64Data || base64Data.length === 0) {
            console.warn('[AudioStreamInterop] processChunk received empty data');
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

        // Log data integrity on first chunk and periodically
        if (_stats.chunksReceived === 1 || (_debug && _stats.chunksReceived % _debugChunkInterval === 0)) {
            var samplesPerChannel = _sourceChannels > 0 ? (float32.length / _sourceChannels) : float32.length;
            var minVal = float32.length > 0 ? float32[0] : 0;
            var maxVal = minVal;
            for (var j = 0; j < float32.length; j++) {
                if (float32[j] < minVal) minVal = float32[j];
                if (float32[j] > maxVal) maxVal = float32[j];
            }
            console.log('[AudioStreamInterop] Chunk #' + _stats.chunksReceived + ' decoded:' +
                ' bytes=' + bytes.length +
                ' float32Samples=' + float32.length +
                ' samplesPerCh=' + samplesPerChannel +
                ' channels=' + _sourceChannels +
                ' range=[' + minVal.toFixed(4) + ', ' + maxVal.toFixed(4) + ']');
        }

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
        console.log('[AudioStreamInterop] Disconnecting... (received=' + _stats.chunksReceived +
            ' played=' + _stats.chunksPlayed + ' errors=' + _stats.errors + ')');
        stopStatsLogger();
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
