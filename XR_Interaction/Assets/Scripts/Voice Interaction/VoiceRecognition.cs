using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using HuggingFace.API;
using UnityEngine.UI;
using System;
using System.IO;
using WebRtcVadSharp;

/// <summary>
/// A simple circular (ring) buffer for float audio data.
/// </summary>
public class CircularBuffer
{
    private float[] buffer;
    private int capacity;
    private int size;
    private int start;
    private int end; // tail of the buffer. Points to the next index after the last element
   

    public CircularBuffer(int capacity)
    {
        this.capacity = capacity;
        this.buffer = new float[capacity];
        this.size = 0;
        this.start = 0;
        this.end = 0;
        
    }

    public void Write(float[] data, int length)
    {
        for (int i = 0; i < length; i ++)
        {
            // Writes the next available spot with data.
            // If capacity is full this implementation will overwrite the oldest element in the buffer with the new data.

            buffer[end] = data[i];
            end = (end + 1) % capacity;


            if (size < capacity)
            {
                // If there is enough space in the buffer just increase the size.
                size += 1;
            }
            else
            {
                // If we ran out of space in the buffer we need to overwrite data.
                // So increase the head or start index of the array to the next index "above" it which is the next oldest element.
                start = (start + 1) % capacity;

            }
        }
    }


    // Get the data in a continuous array
    public float[] GetData()
    {
        float[] data = new float[size];

        if (start < end)
        {
            Array.Copy(buffer, start, data, 0, size);
        }
        else // Buffer is full so we have looped around
        {
            int length_of_first_section = capacity - start;
            Array.Copy(buffer, start, data, 0, length_of_first_section);
            Array.Copy(buffer, 0, data, length_of_first_section, end);

        }

        return data;
    }

    // Clears the buffer resetting pointers to 0.
    public void Clear()
    {
        size = 0;
        start = 0;
        end = 0;
    }
}

public class VoiceRecognition : MonoBehaviour
{   
    [Header("UI")]
    [SerializeField] private Button startButton;
    [SerializeField] private Button stopButton;
    public TMPro.TextMeshProUGUI inputText;
    [SerializeField] private AudioSource audioSource;

    [Header("Voice Interaction")]
    public RunWhisper runWhisper;
    public SentenceSimilarity sentenceSimilarity;
    public VoiceAction voiceAction;
    public CommandManager commandManager;
    public float loudnessThreshold = 0.1f; // Threshold for audio sample to be above to detect a "voice" or noise
    public float silenceDuration = 1f; // Seconds of silence to consider the end of  speech.
    
    #region Microphone Input Variables
    private string micDevice;
    private int sampleRate = 16000;
    private AudioClip clip;
    private byte[] wavData;
    #endregion
    
    #region Voice Detection Variables
    private List<float> speechBuffer = new List<float>(); // Buffer to store audio samples above threshold.
    private float silenceTimer = 0f; // Amount of time elapsed after last audio sample above threshold.
    private int lastSamplePosition = 0; // Last sample position in the audio clip.
    private int currentPosition; // Current sample position in the audio clip.
    private int sampleDiff; // Difference between current and last sample position.
    private bool isRecording; // Flag to check if we have detected a loud enough noise and can start "recording" for ASR.
    private WebRtcVad VAD; // Voice activity detector
    private float[] sampleBuffer; // Preallocated buffer for reading from the AudioClip.
    private CircularBuffer circularBuffer;
    #endregion

    private string recognizedSpeech;

    private void Start()
    {
        // WebRTC initialization if needed
        VAD = new WebRtcVad();
        VAD.OperatingMode = OperatingMode.VeryAggressive;

        recognizedSpeech = "";
        inputText.text = "Initializing...";
        isRecording = false;
        
        // Debug block checking if there are enough microphone devices
        if (Microphone.devices.Length > 0)
        {
            micDevice = Microphone.devices[0];
            Debug.Log("Using microphone: " + micDevice);
        }
        else
        {
            Debug.LogError("No microphone detected!");
            return;
        }

        // **** Starting Mic Input *****
        clip = Microphone.Start(micDevice, true, 10, sampleRate);
        while (Microphone.GetPosition(micDevice) <= 0) { } // Controls latency. 0 means no latency.
        audioSource.clip = clip;
        audioSource.loop = true;
        audioSource.Play();
        inputText.text = "Listening...";

        // Preallocate a reusable sample buffer.
        // We assume a maximum chunk size (for example, 1024 samples per channel).
        sampleBuffer = new float[1024 * clip.channels];

        // Allocate a circular buffer for speech data.
        // Here, we allocate enough space for 10 seconds of audio.
        int maxSpeechSamples = sampleRate * clip.channels * 10;
        circularBuffer = new CircularBuffer(maxSpeechSamples);
        
    }

    private void Update() 
    {   
        // If the microphone is not recording, return
        if (clip == null || !Microphone.IsRecording(micDevice)) 
        {
            return;
        }

        // Get the current recording position of the microphone
        currentPosition = Microphone.GetPosition(micDevice);
        sampleDiff = currentPosition - lastSamplePosition;

        if (sampleDiff < 0)
        {
            // Wrap around to the beginning of the clip if looping
            // means that the clip sample has looped making the current position less than
            // just add the size of the clip samples to get the linear sequenec
            sampleDiff += clip.samples;
        }

        if (sampleDiff > 0)
        {
            /* **** OLD CODE ****
            float[] samples = new float[sampleDiff * clip.channels];
            clip.GetData(samples, lastSamplePosition);
            lastSamplePosition = currentPosition;

            // Check if the audio samples are above the threshold
            ProcessSamples(samples);
            */

            int sampleCount = clip.channels * sampleDiff;

            clip.GetData(sampleBuffer, lastSamplePosition);
            lastSamplePosition = currentPosition;

            ProcessSamples(sampleBuffer, sampleCount);
        }

    }

    private void ProcessSamples(float[] samples, int sampleCount)
    {   
        byte[] pcmData = ConvertToPCM16(samples); // Convert Unity's float samples to 16-bit PCM
        if (VAD.HasSpeech(pcmData, SampleRate.Is16kHz, FrameLength.Is10ms))
        {
            // Start recording if first valid sample detected
            if (!isRecording)
            {
                // Debug.Log("Speech Started");
                isRecording = true;
                silenceTimer = 0f;
            }
            circularBuffer.Write(samples, sampleCount);
        }
        else if (isRecording)
        {
            
            //speechBuffer.AddRange(samples);
            silenceTimer += Time.deltaTime;

            if (silenceTimer >= silenceDuration)
            {
                // Debug.Log("Speech Ended, processing segment.");
                isRecording = false;
                silenceTimer = 0f;
                float[] speech_data = circularBuffer.GetData();
                circularBuffer.Clear();
                runWhisper.TranscribeAudioLocally(speech_data);
                
                // Required to send the data using the Whisper Tiny API in Huggingface
                // Data is required to be in WAV format
                // wavData = EncodeAsWAV(speech_data, clip.frequency, clip.channels);
                // SendRecording();
            }
        }
    }


    /*
    private void StartRecording()
    {
        if (string.IsNullOrEmpty(micDevice))
        {
            Debug.LogError("No microphone available!");
            return;
        }

        inputText.color = Color.white;
        inputText.text = "Recording...";
        startButton.interactable = false;
        stopButton.interactable = true;

        clip = Microphone.Start(micDevice, false, 10, 44100);
        audioSource.clip = clip;
        audioSource.loop = true;
        recording = true;
        
        while (!(Microphone.GetPosition(micDevice) > 0)){}
        // Now that mic has data, start playback
        audioSource.Play();
       
    }
    */

    /*
    private void StopRecording()
    {
       
        var position = Microphone.GetPosition(micDevice);
        Microphone.End(micDevice);
        audioSource.Stop();
        var samples = new float[position * clip.channels];
        clip.GetData(samples, 0);
        wavData = EncodeAsWAV(samples, clip.frequency, clip.channels);
        recording = false;
        
        SendRecording();
    }

    */

    private void SendRecording() {
        inputText.color = Color.yellow;
        inputText.text = "Sending...";
        stopButton.interactable = false;
        
        HuggingFaceAPI.AutomaticSpeechRecognition(wavData, response => {
            inputText.color = Color.white;
            inputText.text = response;
            recognizedSpeech = response;
            startButton.interactable = true;
        }, error => {
            inputText.color = Color.red;
            inputText.text = error;
            recognizedSpeech = "error";
            startButton.interactable = true;
        });
    }


    /// <summary>
    /// Placeholder method for encoding audio samples as WAV.
    /// Replace with your actual implementation.
    /// </summary>
    private byte[] EncodeAsWAV(float[] samples, int frequency, int channels) 
    {
        using (var memoryStream = new MemoryStream(44 + samples.Length * 2)) {
            using (var writer = new BinaryWriter(memoryStream)) {
                writer.Write("RIFF".ToCharArray());
                writer.Write(36 + samples.Length * 2);
                writer.Write("WAVE".ToCharArray());
                writer.Write("fmt ".ToCharArray());
                writer.Write(16);
                writer.Write((ushort)1);
                writer.Write((ushort)channels);
                writer.Write(frequency);
                writer.Write(frequency * channels * 2);
                writer.Write((ushort)(channels * 2));
                writer.Write((ushort)16);
                writer.Write("data".ToCharArray());
                writer.Write(samples.Length * 2);

                foreach (var sample in samples) {
                    writer.Write((short)(sample * short.MaxValue));
                }
            }
            return memoryStream.ToArray();
        }
    }

    /// <summary>
    /// Converts float samples (range -1 to 1) into 16-bit PCM bytes.
    /// </summary>
    private byte[] ConvertToPCM16(float[] samples)
    {
        short[] intData = new short[samples.Length];

        for (int i = 0; i < samples.Length; i++)
        {
            intData[i] = (short)(samples[i] * short.MaxValue); // Convert float (-1 to 1) → short (-32768 to 32767)
        }

        byte[] bytes = new byte[intData.Length * 2]; // Each short (16-bit) needs 2 bytes
        Buffer.BlockCopy(intData, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    void OnDestroy()
    {
        if (Microphone.IsRecording(micDevice))
        {
            Microphone.End(micDevice);
        }
    }

    public string GetRecognizedSpeech()
    {
        return recognizedSpeech;
    }

    public void SetRecognizedSpeech(string transcribed_speech)
    {
        recognizedSpeech = transcribed_speech;
    }

    public void OnTranscriptionSuccess()
    {
        commandManager.UnderstoodCommand(recognizedSpeech, (success, matched_command, value) =>{
            if (!success || matched_command == null)
            {
                // No match found
                return;
            }

            // We have a matched command. Let's invoke it:
            if (matched_command.invokeFunction != null)
            {
                matched_command.invokeFunction.Invoke(matched_command.targetObject, value);
            }
            else
            {
                Debug.LogWarning($"Matched command {matched_command.referencePhrase} has no action assigned!");
            }
        });
    }

}
