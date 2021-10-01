using Microsoft.Xna.Framework.Audio;
using ProtoBuf;
using StrayEngine.Engine.EditorTools;
using StrayEngine.Engine.Referencing;
using StrayEngine.Engine.Serialization;
using StrayEngine.Engine.Systems;
using StrayEngine.ExtensionMethods;
using System;
using System.Collections.Generic;
using System.IO;

namespace StrayEngine.Engine.Audio
{
    /// <summary>
    /// Asset object for a timeline of notes of other audio clips compiled into one
    /// </summary>
    [ProtoContract]
    public class ComposedMusicAudioClip : IAudioClip, ISerializableAtom, ISerializableCallbacks
    {
        public const int DefaultBPM = 120;
        /// <summary>
        /// Used to signal to audio system when trying to replay this effect, whether to reuse the cached buffer or grab it fresh
        /// </summary>
        public bool BufferHasChanged { get; set; }

        private byte[] buffer;
        public byte[] Buffer
        {
            get
            {
                if (bufferIsDirty)
                {
                    RecalculateBuffer();
                }
                return buffer;
            }
        }

        public TimeSpan RawAudioStartTime { get; private set; }

        public TimeSpan RawAudioEndTime { get; private set; }

        public TimeSpan ClipDuration { get; private set; }

        /// <summary>
        /// Index the clip starts at
        /// </summary>
        public int BufferIndexStart { get; private set; }
        /// <summary>
        /// Index the clip ends at
        /// </summary>
        public int BufferIndexEnd { get; private set; }
        /// <summary>
        /// Length of the clip in the buffer
        /// </summary>

        public int BufferIndexLength { get; private set; }

        [ProtoMember(1)]
        public AudioChannels AudioChannel { get; set; }

        [ProtoMember(2)]
        public int SampleRate { get; set; }

        [ProtoMember(3)]
        public string Name { get; set; }


        //Cant use BPM with floats, or ProtoMember(4) its deprecated now but old data might have it, leaving it here commented out incase old data comes in and someone is like "what is this"
        /*
        private float bpm;
        [ProtoMember(4)]
        public float BPM
        {
            get { return bpm; }
            set { bpm = value; bufferIsDirty = true; }
        }
        */

        /// <summary>
        /// Deprecated, moved to asset tool, leaving here to support old data
        /// </summary>
        [ProtoMember(5)]
        public string texturename { get; set; }
        [ProtoMember(6)]
        public List<ComposedMusicAudioClipEntryKey> NoteEntries { get; set; }
        [ProtoMember(7)]
        public byte NoteForSmallestTimeStep { get; set; } = 16;
        [ProtoMember(8)]
        public byte BeatsPerMeasure { get; set; } = 4;
        [ProtoMember(9)]
        public byte NoteThatGetsFullBeat { get; set; } = 4;

        private int bpm;
        [ProtoMember(10)]
        public int BPM
        {
            get { return bpm; }
            set { bpm = value; bufferIsDirty = true; }
        }

        private Dictionary<int, ComposedMusicAudioClipInstrumentEntry> instruments;

        private Dictionary<CachedAudioClipKey, AudioClip> instrumentToNoteToDurationClipCache;


        private bool bufferIsDirty;

        public ulong SerializedAssetId { get; set; }

        public ISystemProvider OriginSystemProvider { get; private set; }


        /// <summary>
        /// Deserialization only
        /// </summary>
        public ComposedMusicAudioClip()
        {
            NoteEntries = new List<ComposedMusicAudioClipEntryKey>();

            instruments = new Dictionary<int, ComposedMusicAudioClipInstrumentEntry>();
            instrumentToNoteToDurationClipCache = new Dictionary<CachedAudioClipKey, AudioClip>();
        }

        /// <summary>
        /// Construct a new empty ComposedAudioClip with the given parameters
        /// </summary>
        /// <param name="audioChannel"></param>
        /// <param name="sampleRate"></param>
        /// <param name="bpm"></param>
        /// <param name="systemProvider"></param>
        public ComposedMusicAudioClip(AudioChannels audioChannel, int sampleRate, int bpm, ISystemProvider systemProvider)
            : this()
        {
            this.AudioChannel = audioChannel;
            this.SampleRate = sampleRate;
            AudioClipTool audioClipTool = systemProvider.GetSystem<IEditorToolsSystem>().GetToolWithAssetState<AudioClipTool>(this);
            
            this.Name = "New Song";
            //Should probably switch this to a parameter at some point
            this.texturename = "music"; 
            this.BPM = bpm;
            OriginSystemProvider = systemProvider;
        }

        #region Helper methods used at edit time
        /// <summary>
        /// Adds an instrument to the cache, returns the id of the instrument.  Needs to be called before adding notes for that instrument
        /// </summary>
        /// <param name="instrumentSource"></param>
        /// <returns></returns>
        public int AddInstrument(AudioClip instrumentSource)
        {
            int attemptIndex = 0;
            ComposedMusicAudioClipInstrumentEntry instrumentToAdd = new ComposedMusicAudioClipInstrumentEntry(OriginSystemProvider.GetSystem<IAssetCacheSystem>().RequestHandleForAsset<AudioClip>(instrumentSource));
            while (true)
            {
                if (!instruments.ContainsKey(attemptIndex))
                {
                    instruments[attemptIndex] = instrumentToAdd;
                    break;
                }
                attemptIndex++;
            }
            return attemptIndex;
        }
        /// <summary>
        /// Adds a note of given duration and pitch and start time to the song.  Instrument is the id of the instrument returned when AddInstrument was called.  Returns a wrapper class with info about the added note.
        /// </summary>
        /// <param name="instrument"></param>
        /// <param name="beatIndex"></param>
        /// <param name="midiNote"></param>
        /// <param name="subBeatStep"></param>
        /// <param name="noteDurationId"></param>
        /// <param name="isDotted"></param>
        /// <returns></returns>
        public ComposedMusicAudioClipEntryKey AddNote(int instrument, int beatIndex, int midiNote, int subBeatStep, int noteDurationId, bool isDotted)
        {
            if (!instruments.ContainsKey(instrument))
            {
                throw new InvalidOperationException($"Can't add note for instrument with id {0} since it doesn't exist");
            }
            ComposedMusicAudioClipEntryKey retVal = new ComposedMusicAudioClipEntryKey(instrument, beatIndex, midiNote, subBeatStep, noteDurationId, isDotted);
            NoteEntries.Add(retVal);
            bufferIsDirty = true;
            return retVal;
        }

        /// <summary>
        /// Checks if we can add a note here without overlapping one of the same instrument, time, duration and pitch.  If so we probably don't want to add it cause I have no idea what will happen if we do that =D 
        /// </summary>
        /// <param name="instrument"></param>
        /// <param name="beatIndex"></param>
        /// <param name="midiNote"></param>
        /// <param name="subBeatStep"></param>
        /// <param name="noteDurationId"></param>
        /// <param name="isDotted"></param>
        /// <returns></returns>
        public bool AddingNoteWouldOverlap(int instrument, int beatIndex, int midiNote, int subBeatStep, int noteDurationId, bool isDotted)
        {
            int samplesPerBeat = GetNumberOfStepsInABeat();
            int proposedStepLength = GetStepDurationOfNote(noteDurationId, isDotted);
            int stepStart = (beatIndex * samplesPerBeat) + subBeatStep;
            int stepEnd = stepStart + proposedStepLength;

            List<ComposedMusicAudioClipEntryKey> notesForInstrument = GetNotesForInstrument(instrument);
            foreach(ComposedMusicAudioClipEntryKey note in notesForInstrument)
            {
                if(note.MidiNoteNumber != midiNote)
                {
                    continue;
                }
                int checkStart = (note.BeatIndex * samplesPerBeat) + note.SubBeatStep;
                int checkEnd = checkStart + GetStepDurationOfNote(note.NoteLengthId, note.IsDotted);
                int xCheck = Math.Max(stepStart, checkStart);
                int yCheck = Math.Min(stepEnd, checkEnd);
                if(xCheck < yCheck)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Removes an entire instrument and all note entries.  Marks buffer as dirty.
        /// </summary>
        /// <param name="instrument"></param>
        public void RemoveInstrument(int instrument)
        {
            List<ComposedMusicAudioClipEntryKey> listToDelete = new List<ComposedMusicAudioClipEntryKey>();
            for (int i = 0; i < NoteEntries.Count; i++)
            {
               if(NoteEntries[i].InstrumentIndex == instrument)
                {
                    listToDelete.Add(NoteEntries[i]);
                }
            }
            for(int j = 0;j < listToDelete.Count;j++)
            {
                NoteEntries.Remove(listToDelete[j]);
            }
            instruments.Remove(instrument);
            bufferIsDirty = true;
        }

        /// <summary>
        /// Removes a given note.  Marks buffer as dirty.
        /// </summary>
        /// <param name="instrument"></param>
        /// <param name="beatIndex"></param>
        /// <param name="midiNote"></param>
        /// <param name="subBeatStep"></param>
        public void RemoveNote(int instrument, int beatIndex, int midiNote, int subBeatStep)
        {
            int toRemove = 0;
            for(;toRemove < NoteEntries.Count; toRemove++)
            {
                ComposedMusicAudioClipEntryKey cur = NoteEntries[toRemove];
                if(cur.InstrumentIndex == instrument && cur.BeatIndex == beatIndex && cur.MidiNoteNumber == midiNote && cur.SubBeatStep == subBeatStep)
                {
                    NoteEntries.RemoveAt(toRemove);
                    bufferIsDirty = true;
                    break;
                }
            }
        }
        /// <summary>
        /// Returns all placed notes for the given instrument
        /// </summary>
        /// <param name="instrument"></param>
        /// <returns></returns>
        public List<ComposedMusicAudioClipEntryKey> GetNotesForInstrument(int instrument)
        {
            if (!instruments.ContainsKey(instrument))
            {
                throw new InvalidOperationException($"Can't get notes for instrument with id {0} since it doesn't exist");
            }

            List<ComposedMusicAudioClipEntryKey> retVal = new List<ComposedMusicAudioClipEntryKey>();
            foreach(ComposedMusicAudioClipEntryKey entry in NoteEntries)
            {
                if(entry.InstrumentIndex == instrument)
                {
                    retVal.Add(entry);
                }
            }
            return retVal;
        }

        /// <summary>
        /// Returns all registered instruments, keyed by their ids.
        /// </summary>
        /// <returns></returns>
        public Dictionary<int, ComposedMusicAudioClipInstrumentEntry> GetAllInstruments()
        {
            return new Dictionary<int, ComposedMusicAudioClipInstrumentEntry>(instruments);
        }
        /// <summary>
        /// Sets the volume of an instrument.  Refreshes the generated notes cache.
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <param name="volume"></param>
        public void SetInstrumentVolume(int instrumentId, float volume)
        {
            ComposedMusicAudioClipInstrumentEntry instrument = instruments[instrumentId];
            if(float.Equals(volume, instrument.Volume))
            {
                return;
            }
            instrument.Volume = Math.Max(0f, Math.Min(1f, volume));
            HandleCacheRefreshFromInstrumentChange(instrumentId);

        }

        /// <summary>
        /// Changes an instrument to use a different source clip, keeping all placed notes but changing what sfx source they use.  Refreshes the generated notes cache.
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <param name="newClip"></param>
        public void ChangeInstrumentClip(int instrumentId, AudioClip newClip)
        {
            ComposedMusicAudioClipInstrumentEntry instrument = instruments[instrumentId];

            OriginSystemProvider.GetSystem<IAudioManagerSystem>().QueueAudioClipForDisposal(instrument.InstrumentAsset.Ref);
            instrument.InstrumentAsset.DisposeHandle();
            instrument.InstrumentAsset = OriginSystemProvider.GetSystem<IAssetCacheSystem>().RequestHandleForAsset<AudioClip>(newClip);
            HandleCacheRefreshFromInstrumentChange(instrumentId);
        }

        /// <summary>
        /// Shifts all placed notes for an instrument by the given number of notes.  Refreshes generated notes cache.
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <param name="midiNoteShiftAmount"></param>
        public void ShiftAllNotesForInstrument(int instrumentId, int midiNoteShiftAmount)
        {
            List<ComposedMusicAudioClipEntryKey> notes = GetNotesForInstrument(instrumentId);
            foreach(ComposedMusicAudioClipEntryKey note in notes)
            {
                note.MidiNoteNumber += midiNoteShiftAmount;
            }
            HandleCacheRefreshFromInstrumentChange(instrumentId);
        }

        /// <summary>
        /// Clears all cached generated clips for the given instrument, they'll need to be regenerated later.  Sets buffer to dirty.
        /// </summary>
        /// <param name="instrumentId"></param>
        private void HandleCacheRefreshFromInstrumentChange(int instrumentId)
        {
            IAudioManagerSystem audioManager = OriginSystemProvider.GetSystem<IAudioManagerSystem>();
            List<CachedAudioClipKey> toRemove = new List<CachedAudioClipKey>();
            foreach(KeyValuePair<CachedAudioClipKey, AudioClip> cached in instrumentToNoteToDurationClipCache)
            {
                if(cached.Key.InstrumentId == instrumentId)
                {
                    audioManager.QueueAudioClipForDisposal(cached.Value);
                    toRemove.Add(cached.Key);
                }
            }
            foreach(CachedAudioClipKey remove in toRemove)
            {
                instrumentToNoteToDurationClipCache.Remove(remove);
            }
            bufferIsDirty = true;
        }

        /// <summary>
        /// Finds the beat that the final note finishes playing
        /// </summary>
        /// <returns></returns>
        public int GetLongestLengthInBeats()
        {
            int numberOfStepsPerBeat = GetNumberOfStepsInABeat();
            int highestStep = 0;
            foreach(ComposedMusicAudioClipEntryKey note in NoteEntries)
            {
                int startStep = (note.BeatIndex * numberOfStepsPerBeat) + note.SubBeatStep;
                int stepDuration = GetStepDurationOfNote(note.NoteLengthId, note.IsDotted);
                int endStep = startStep + stepDuration;
                if(endStep > highestStep)
                {
                    highestStep = endStep;
                }
            }
            return (highestStep / numberOfStepsPerBeat) + 1;
        }

        /// <summary>
        /// Gets the clip for the instrumentid
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <returns></returns>
        private AudioClip GetAudioClipFromCache(int instrumentId)
        {
            ComposedMusicAudioClipInstrumentEntry instrumentEntry = instruments[instrumentId];
            return instrumentEntry.InstrumentAsset.Ref;
        }

        /// <summary>
        /// Returns the AudioClip generated for the given instrument, note and duration.
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <param name="midiNoteId"></param>
        /// <param name="stepDuration"></param>
        /// <param name="secondsDurationPerStep"></param>
        /// <param name="volume"></param>
        /// <returns></returns>
        public AudioClip GetNotedAudioClipFromCache(int instrumentId, int midiNoteId, int stepDuration, float secondsDurationPerStep, float volume)
        {
            CachedAudioClipKey key = new CachedAudioClipKey(instrumentId, midiNoteId, stepDuration, volume);

            AudioClip tryGetForNote;
            if (!instrumentToNoteToDurationClipCache.TryGetValue(key, out tryGetForNote))
            {
                AudioClip forInstrument = GetAudioClipFromCache(instrumentId);
                float p_base_freq = (float)(Math.Pow(2, (float)(midiNoteId - 48) / 24f)) * 0.19f;
                SFXRSoundParams baseParms = forInstrument.parms.Copy();
                baseParms.BaseFreq = p_base_freq;
                baseParms.SFXRVolume = volume;
                ComposedMusicAudioClipInstrumentEntry instrument = GetAllInstruments()[instrumentId];
                SFXRModifiedEnvelopeSettings modifiedEnvelopeSettings = instrument.GetEnvelopeSettings(stepDuration * secondsDurationPerStep, SampleRate);

                baseParms.EnvSustain = modifiedEnvelopeSettings.Sustain;
                baseParms.EnvAttack = modifiedEnvelopeSettings.Attack;
                baseParms.EnvDecay = modifiedEnvelopeSettings.Decay;

                tryGetForNote = new AudioClip(baseParms, $"{instrumentId}_{midiNoteId}", forInstrument.texturename, OriginSystemProvider);
                instrumentToNoteToDurationClipCache.Add(key, tryGetForNote);
            }
            return tryGetForNote;
        }

        /// <summary>
        /// Helper for UI, returns the letter name for the given midi note.
        /// </summary>
        /// <param name="midiId"></param>
        /// <param name="includeOctave"></param>
        /// <returns></returns>
        public string GetNoteNameForMidiId(int midiId, bool includeOctave)
        {
            //12 notes in a scale, A-G is 7, then 5 sharps
            //Midi scale starts lower than keyboard, midi=21 is piano:1 (1 indexed), we're doing 0 indexed
            //Midi 21, Piano 1, A0
            //Midi 22, Piano 2, A#0
            //Midi 23, Piano 3, B0
            //Midi 24, Piano 4, C1
            int octaveNumber = 0;
            string letterName = "";
            if (midiId < 21)
            {
                letterName = $"?{midiId}?";
                //throw new NotSupportedException($"Midi id {midiId} is not supported");
            }

            if (midiId == 21)
            {
                letterName = "A";
            }
            else if (midiId == 22)
            {
                letterName = "A#";
            }
            else if (midiId == 23)
            {
                letterName = "B";
            }
            else
            {
                int pianoScale0Based = midiId - 21;
                int pianoScaleFirstCAt0 = midiId - 24;
                octaveNumber = (pianoScaleFirstCAt0 / 12) + 1;
                int letterCode = pianoScale0Based % 12;
                switch (letterCode)
                {
                    case 0:
                        letterName = "A";
                        break;
                    case 1:
                        letterName = "A#";
                        break;
                    case 2:
                        letterName = "B";
                        break;
                    case 3:
                        letterName = "C";
                        break;
                    case 4:
                        letterName = "C#";
                        break;
                    case 5:
                        letterName = "D";
                        break;
                    case 6:
                        letterName = "D#";
                        break;
                    case 7:
                        letterName = "E";
                        break;
                    case 8:
                        letterName = "F";
                        break;
                    case 9:
                        letterName = "F#";
                        break;
                    case 10:
                        letterName = "G";
                        break;
                    case 11:
                        letterName = "G#";
                        break;
                }
            }
            if (includeOctave)
            {
                return letterName + octaveNumber.ToString();
            }
            else
            {
                return letterName;
            }

        }

        /// <summary>
        /// Get the duration of a single step in seconds
        /// </summary>
        /// <returns></returns>
        public float GetSecondsDurationOfStep()
        {
            float beatsPerSecond = BPM / 60f;
            int stepsPerBeat = (NoteForSmallestTimeStep / NoteThatGetsFullBeat);
            float stepsPerSecond = beatsPerSecond * stepsPerBeat;
            float secondsDurationOfStep = 1f / stepsPerSecond;

            return secondsDurationOfStep;
        }

        /// <summary>
        /// Gets how many steps a note is
        /// </summary>
        /// <param name="note"></param>
        /// <param name="dottedNote"></param>
        /// <returns></returns>
        public int GetStepDurationOfNote(int note, bool dottedNote)
        {
            int retVal = NoteForSmallestTimeStep / note;
            if (dottedNote)
            {
                retVal += NoteForSmallestTimeStep / (note * 2);
            }
            return retVal;
        }

        /// <summary>
        /// Helper math, number of steps in a beat
        /// </summary>
        /// <returns></returns>
        public int GetNumberOfStepsInABeat()
        {
            return NoteForSmallestTimeStep / NoteThatGetsFullBeat;
        }
        #endregion

        /// <summary>
        /// Regenerates the WAV buffer.  Uses cached instrument wav clips.
        /// </summary>
        public void RecalculateBuffer()
        {
            if (!bufferIsDirty)
            {
                return;
            }

            if(NoteEntries.Count == 0)
            {
                buffer = new byte[] { 0, 0 };
                bufferIsDirty = false;
                BufferHasChanged = true;
                return;
            }
            int samplesPerSecond = SampleRate * (int)AudioChannel;
            float timeIncrement = 1 / (float)samplesPerSecond;
            int stepsPerBeat = (NoteForSmallestTimeStep / NoteThatGetsFullBeat);
            float beatsPerSecond = BPM / 60f;
            float stepsPerSecond = beatsPerSecond * stepsPerBeat;
            float secondsDurationOfStep = 1f / stepsPerSecond;
            int samplesPerStep = (int)(samplesPerSecond * secondsDurationOfStep);
            int samplesPerBeat = samplesPerStep * stepsPerBeat;

            //First group by "beats" which should each have a standard start time
            

            List<ComposedMusicAudioClipEntryKey> clipsSortedByBeatIndex = new List<ComposedMusicAudioClipEntryKey>(NoteEntries);
            clipsSortedByBeatIndex.Sort((a, b) => { return a.BeatIndex.CompareTo(b.BeatIndex); });

            List<SamplingSoundEffect> samplingListSortedByStartSampleIndex = new List<SamplingSoundEffect>();

            int currentBeatSampleStartIndex = 0;
            int currentBeat = 0;
            for(int i = 0; i < clipsSortedByBeatIndex.Count; i++)
            {
                ComposedMusicAudioClipEntryKey curEntry = clipsSortedByBeatIndex[i];
                if(curEntry.BeatIndex != currentBeat)
                {
                    currentBeat = curEntry.BeatIndex;
                    currentBeatSampleStartIndex = currentBeat * samplesPerBeat;
                }
                ComposedMusicAudioClipInstrumentEntry instrument = instruments[curEntry.InstrumentIndex];
                float instrumentSecondsOffset = instrument.StartOffset;
                int samplesOffset = (int)(samplesPerSecond * instrumentSecondsOffset);
                int noteDuration = NoteForSmallestTimeStep / curEntry.NoteLengthId;
                if (curEntry.IsDotted)
                {
                    int toIncrease = noteDuration / 2;
                    noteDuration += toIncrease;
                }

                samplingListSortedByStartSampleIndex.Add(new SamplingSoundEffect(currentBeatSampleStartIndex + (curEntry.SubBeatStep * samplesPerStep) + samplesOffset, 
                    GetNotedAudioClipFromCache(curEntry.InstrumentIndex, curEntry.MidiNoteNumber, noteDuration, secondsDurationOfStep, instrument.Volume), 2));
            }

            samplingListSortedByStartSampleIndex.Sort((a, b) => { return a.StartGlobalSampleIndex.CompareTo(b.StartGlobalSampleIndex); });
            //Need to find the expected end ,so can presize the list as an optimization
            SamplingSoundEffect lastSample = samplingListSortedByStartSampleIndex[samplingListSortedByStartSampleIndex.Count - 1];
            int probableEndNumberOfSamples = lastSample.StartGlobalSampleIndex + lastSample.NumberOfSamples;
            List<int> outputBuffer = new List<int>(probableEndNumberOfSamples);

            LinkedList<SamplingSoundEffect> sampleLinkedList = new LinkedList<SamplingSoundEffect>(samplingListSortedByStartSampleIndex);
            List<SamplingSoundEffect> toRemoveSamples = new List<SamplingSoundEffect>();
            int curSampleIndex = 0;
            int highestMerged = int.MinValue;
            while(sampleLinkedList.Count > 0)
            {
                int curSample = 0;
                int numOfLayersInSample = 0;
                toRemoveSamples.Clear();
                foreach(SamplingSoundEffect curSampled in sampleLinkedList)
                {
                    short localSample = 0;
                    SamplingSoundEffect.SampleState sampleState = curSampled.TryGetShortSampleForGlobalIndex(curSampleIndex, out localSample);
                    if(sampleState == SamplingSoundEffect.SampleState.NotStarted)
                    {
                        //Since our list is sorted, and this one isn't ready to start yet, we know everything after this will also return not ready to start, so go to next loop  in while
                        break;
                    }
                    else if(sampleState == SamplingSoundEffect.SampleState.Finished)
                    {
                        //There might be more after this still playing, but need to mark for removal
                        toRemoveSamples.Add(curSampled);
                    }
                    else
                    {
                        curSample += localSample;
                        numOfLayersInSample++;
                    }
                }

                if(numOfLayersInSample == 0)
                {
                    outputBuffer.Add(0);
                }
                else
                {
                    outputBuffer.Add(curSample);
                    int absCur = Math.Abs(curSample);
                    if(absCur > highestMerged)
                    {
                        highestMerged = absCur;
                    }
                }

                //Now remove from to remove
                foreach(SamplingSoundEffect toRemove in toRemoveSamples)
                {
                    sampleLinkedList.Remove(toRemove);
                }
                curSampleIndex++;
            }

            if(highestMerged > short.MaxValue)
            {
                float toChange = (float)short.MaxValue / (float)highestMerged;
                for(int i = 0; i < outputBuffer.Count; i++)
                {
                    outputBuffer[i] = (int)(outputBuffer[i] * toChange);
                }
            }
            List<short> shortBuffer = new List<short>(outputBuffer.Count);
            foreach(int intVal in outputBuffer)
            {
                shortBuffer.Add(Convert.ToInt16(intVal));
            }
            MemoryStream ms = new MemoryStream();
            for(int i = 0; i < shortBuffer.Count; i++)
            {
                
                byte[] asBytes = BitConverter.GetBytes(shortBuffer[i]);
                ms.Write(asBytes, 0, 2);
            }
            buffer = ms.ToArray();
            RawAudioStartTime = TimeSpan.Zero;
            // /2 cause 2 bytes to a sample
            float durationInSeconds = (buffer.Length / 2) / (float)samplesPerSecond;
            RawAudioEndTime = TimeSpan.FromSeconds(durationInSeconds);
            ClipDuration = RawAudioEndTime;
            ms.Dispose();
            bufferIsDirty = false;
            BufferHasChanged = true;
        }

        

        #region Serialization and assets

        public void AssembleUnloadGroup(HashSet<ISerializableReferenceAsset> allToUnload)
        {
            
        }

        public void HandleAssetUnloaded()
        {
            
        }

        public void RetrieveAllHandles(List<ObjectHandle> allHandlesOwned)
        {
            
        }

        public void OnBeforeSerialize(StraySerializationContext context)
        {

        }

        public void OnAfterDeserialize(StraySerializationContext context)
        {
            this.OriginSystemProvider = context.SystemProvider;
            if(BPM == 0)
            {
                BPM = DefaultBPM;
            }
        }

        public void RetrieveSerializableAtoms(List<object> traceResult)
        {
            traceResult.Add(instruments);
        }

        public void ReconstructFromAtoms(LinkedListNode<object> thisNode, ref int currentIndex, out int totalChildren)
        {
            totalChildren = 1;
            instruments = (Dictionary<int, ComposedMusicAudioClipInstrumentEntry>)thisNode.ValueAtOffset(currentIndex + 1);
        }
        #endregion
    }

    /// <summary>
    /// Key for a cached AudioClip based on instrumentId, noteId, the duration of the note, and volume
    /// </summary>
    public struct CachedAudioClipKey : IEquatable<CachedAudioClipKey>
    {
        public int InstrumentId;
        public int NoteId;
        public int StepDuration;
        public float Volume;

        public CachedAudioClipKey(int instrument, int note, int duration, float volume)
        {
            InstrumentId = instrument;
            NoteId = note;
            StepDuration = duration;
            Volume = volume;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is CachedAudioClipKey && Equals((CachedAudioClipKey)obj);
        }

        public bool Equals(CachedAudioClipKey other)
        {
            return this.InstrumentId == other.InstrumentId && this.NoteId == other.NoteId && this.StepDuration == other.StepDuration && this.Volume == other.Volume;
        }

        public override int GetHashCode()
        {
            unchecked // Overflow is fine, just wrap
            {
                int hash = 17;
                hash = hash * 23 + InstrumentId.GetHashCode();
                hash = hash * 23 + NoteId.GetHashCode();
                hash = hash * 23 + StepDuration.GetHashCode();
                hash = hash * 23 + Volume.GetHashCode();
                return hash;
            }
        }
    }

    /// <summary>
    /// Key for a single note placed in the song, storing instrument, duration, and note.
    /// </summary>
    [ProtoContract]
    public class ComposedMusicAudioClipEntryKey
    {
        [ProtoMember(1)]
        public int InstrumentIndex { get; set; }
        [ProtoMember(2)]
        public int MidiNoteNumber { get; set; }
        [ProtoMember(3)]
        public int BeatIndex { get; set; }
        [ProtoMember(4)]
        public int SubBeatStep { get; set; }
        [ProtoMember(5)]
        public int NoteLengthId { get; set; }
        [ProtoMember(6)]
        public bool IsDotted { get;set; }

        public ComposedMusicAudioClipEntryKey()
        {

        }

        public ComposedMusicAudioClipEntryKey(int instrumentIndex, int beatIndex, int midiNoteNumber, int subBeatStep, int noteLengthId, bool isDotted)
        {
            this.InstrumentIndex = instrumentIndex;
            this.BeatIndex = beatIndex;
            this.MidiNoteNumber = midiNoteNumber;
            this.SubBeatStep = subBeatStep;
            this.NoteLengthId = noteLengthId;
            this.IsDotted = isDotted;
        }
    }

    /// <summary>
    /// Normally a generated SFX has a defined length based on the parameters.  Those parameters include "Attack" "Sustain" "Decay" so basically "Intro" "Body" "Exit" of the sfx.
    /// Notes need to have a specific duration, so we need to modify these to fit that duration, though there's multiple ways to do that.
    /// None: Used for percussion mostly, we keep the duration the same so it won't necessarily fit the duration of the note you're setting
    /// EnvelopeSustainOnly: we stretch/shrink the middle portion of the sfx, leaving Intro and Exit the same.  Might end up with a longer-than-intended note if the total of Attack and Decay is longer than the duration required
    /// EnvelopeAll: Take the ratio of Attack, Sustain, Decay and keep it the same as we increase/decrease them proportionally
    /// </summary>
    public enum InstrumentDurationModificationMode
    {
        None, EnvelopeSustainOnly, EnvelopeAll
    }

    /// <summary>
    /// Container class for all the modified settings of a given instrument, letting you change how its played.
    /// </summary>
    public struct SFXRModifiedEnvelopeSettings
    {
        public float Attack;
        public float Decay;
        public float Sustain;

        public SFXRModifiedEnvelopeSettings(float attack, float decay, float sustain)
        {
            Attack = attack;
            Decay = decay;
            Sustain = sustain;
        }
    }

    /// <summary>
    /// Registered instrument, storing it within this class lets us set some global override settings like volume, or allows us to take an SFX and use an instrument that's just a subset of that sfx as well
    /// </summary>
    [ProtoContract]
    public class ComposedMusicAudioClipInstrumentEntry : ISerializableAtom
    {
        [ProtoMember(1)]
        public float Volume { get; set; }
        [ProtoMember(2)]
        public float StartOffset { get; set; }
        [ProtoMember(3)]
        public InstrumentDurationModificationMode DurationMode { get; set; }

        public ObjectHandle<AudioClip> InstrumentAsset { get; set; }

        public ComposedMusicAudioClipInstrumentEntry()
        {

        }
        public ComposedMusicAudioClipInstrumentEntry(ObjectHandle<AudioClip> instrument)
        {
            Volume = 0.3f;
            StartOffset = 0f;
            DurationMode = InstrumentDurationModificationMode.EnvelopeAll;
            InstrumentAsset = instrument;
        }

        /// <summary>
        /// Gets the settings package that SFXR needs to actually generate the wav
        /// </summary>
        /// <param name="timeInSecondsNoteDurationNeeded"></param>
        /// <param name="parentSampleRate"></param>
        /// <returns></returns>
        public SFXRModifiedEnvelopeSettings GetEnvelopeSettings(float timeInSecondsNoteDurationNeeded, int parentSampleRate)
        {
            SFXRSoundParams parms = InstrumentAsset.Ref.parms;

            if(DurationMode == InstrumentDurationModificationMode.None)
            {
                return new SFXRModifiedEnvelopeSettings(parms.EnvAttack, parms.EnvSustain, parms.EnvDecay);
            }
            else if(DurationMode == InstrumentDurationModificationMode.EnvelopeSustainOnly)
            {
                float timeInSustainValues = (float)Math.Sqrt(timeInSecondsNoteDurationNeeded * parentSampleRate / 100000.0f);
                return new SFXRModifiedEnvelopeSettings(parms.EnvAttack, timeInSustainValues, parms.EnvDecay);
            }
            else if(DurationMode == InstrumentDurationModificationMode.EnvelopeAll)
            {
                float secondsOfAttack = (parms.EnvAttack * parms.EnvAttack) * 100000.0f / parentSampleRate;
                float secondsOfSustain = (parms.EnvSustain * parms.EnvSustain) * 100000.0f / parentSampleRate;
                float secondsOfDecay = (parms.EnvDecay * parms.EnvDecay) * 100000.0f / parentSampleRate;

                float totalSecondsDuration = secondsOfAttack + secondsOfSustain + secondsOfDecay;
                float percAttack = secondsOfAttack / totalSecondsDuration;
                float percSustain = secondsOfSustain / totalSecondsDuration;
                float percDecay = secondsOfDecay / totalSecondsDuration;

                float newSecondsOfAttack = timeInSecondsNoteDurationNeeded * percAttack;
                float newSecondsOfSustain = timeInSecondsNoteDurationNeeded * percSustain;
                float newSecondsOfDecay = timeInSecondsNoteDurationNeeded * percDecay;

                float sfxrAttack = (float)Math.Sqrt(newSecondsOfAttack * parentSampleRate / 100000.0f);
                float sfxrSustain = (float)Math.Sqrt(newSecondsOfSustain * parentSampleRate / 100000.0f);
                float sfxrDecay = (float)Math.Sqrt(newSecondsOfDecay * parentSampleRate / 100000.0f);

                return new SFXRModifiedEnvelopeSettings(sfxrAttack, sfxrDecay, sfxrSustain);
            }
            throw new NotSupportedException($"SFXR Duration Mod Mode of type {DurationMode.ToString()}");
        }

        public void RetrieveSerializableAtoms(List<object> traceResult)
        {
            if (InstrumentAsset != null)
            {
                traceResult.Add(InstrumentAsset);
            }
        }

        public void ReconstructFromAtoms(LinkedListNode<object> thisNode, ref int currentIndex, out int totalChildren)
        {
            totalChildren = 1;
            InstrumentAsset = (ObjectHandle<AudioClip>)thisNode.ValueAtOffset(currentIndex + 1);
        }
    }

    /// <summary>
    /// Used to store where all the sound effects are placed in the timeline, helping the Compile method know when to read from them.
    /// </summary>
    public class SamplingSoundEffect
    {
        public enum SampleState { NotStarted, Playing, Finished }
        public int StartGlobalSampleIndex { get; set; }
        //Inclusive, ie you DO read this value
        public int InternalBufferStartIndex { get; set; }
        //Inclusive, ie you DO read this value
        public int InternalBufferEndIndex { get; set; }
        //Length would be End - Start + 1  (if start at 1, end at 3, you'd be reading 1 2 and 3 so need the +1

        public int NumberOfSamples { get; private set; }

        private AudioClip clip;

        public SamplingSoundEffect(int startGlobalSampleIndex, AudioClip clip, int bytesPerSample)
        {
            StartGlobalSampleIndex = startGlobalSampleIndex;

            this.clip = clip;
            this.InternalBufferStartIndex = clip.BufferIndexStart;
            this.InternalBufferEndIndex = clip.BufferIndexEnd;
            this.NumberOfSamples = clip.BufferIndexLength / bytesPerSample;
        }

        public SampleState TryGetByteSampleForGlobalIndex(int globalIndex, out byte sample)
        {
            sample = 0;
            if(globalIndex < StartGlobalSampleIndex)
            {
                //Too early
                return SampleState.NotStarted;
            }
            
            if(globalIndex >= StartGlobalSampleIndex + NumberOfSamples)
            {
                //We finished
                return SampleState.Finished;
            }

            int internalIndex = globalIndex - StartGlobalSampleIndex;
            sample = clip.Buffer[internalIndex];
            return SampleState.Playing;
        }

        public SampleState TryGetShortSampleForGlobalIndex(int globalIndex, out short sample)
        {
            sample = 0;
            
            if (globalIndex < StartGlobalSampleIndex)
            {
                //Too early
                return SampleState.NotStarted;
            }

            if (globalIndex >= StartGlobalSampleIndex + NumberOfSamples)
            {
                //We finished
                return SampleState.Finished;
            }
            int internalByteIndex = (globalIndex - StartGlobalSampleIndex);
            internalByteIndex = internalByteIndex * 2; //Double it up since for every sample we read 2 bytes
            internalByteIndex = internalByteIndex + InternalBufferStartIndex;
            sample = BitConverter.ToInt16(clip.Buffer, internalByteIndex);
            return SampleState.Playing;
        }
    }
}
