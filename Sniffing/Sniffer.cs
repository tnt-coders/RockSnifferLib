﻿using RockSnifferLib.Cache;
using RockSnifferLib.Events;
using RockSnifferLib.Logging;
using RockSnifferLib.RSHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace RockSnifferLib.Sniffing
{
    public class Sniffer
    {
        /// <summary>
        /// Fired when the Sniffer state has changed
        /// </summary>
        public event EventHandler<OnStateChangedArgs> OnStateChanged;

        /// <summary>
        /// Fired when the current song details have changed
        /// </summary>
        public event EventHandler<OnSongChangedArgs> OnSongChanged;

        /// <summary>
        /// Fired after each successful memory readout
        /// </summary>
        public event EventHandler<OnMemoryReadoutArgs> OnMemoryReadout;

        /// <summary>
        /// The current state of rocksmith, initial state is IN_MENUS
        /// </summary>
        public SnifferState currentState = SnifferState.NONE;
        private SnifferState previousState = SnifferState.NONE;

        /// <summary>
        /// Currently active cdlc details
        /// </summary>
        private SongDetails currentCDLCDetails = new SongDetails();

        /// <summary>
        /// Currently active memory readout
        /// </summary>
        private RSMemoryReadout currentMemoryReadout = new RSMemoryReadout();

        /// <summary>
        /// Reference to the rocksmith process
        /// </summary>
        private Process rsProcess;

        /// <summary>
        /// Cache to use
        /// </summary>
        private ICache cache;

        /// <summary>
        /// The memory reader
        /// </summary>
        private RSMemoryReader memReader;

        /// <summary>
        /// Boolean to let async tasks finish
        /// </summary>
        private bool running = true;

        private FileSystemWatcher watcher;

        /// <summary>
        /// Instantiate a new Sniffer on process, using cache
        /// </summary>
        /// <param name="rsProcess"></param>
        /// <param name="cache"></param>
        public Sniffer(Process rsProcess, ICache cache)
        {
            this.rsProcess = rsProcess;
            this.cache = cache;

            memReader = new RSMemoryReader(rsProcess);

            DoMemoryReadout();
            DoStateMachine();
            DoSniffing();
        }

        private async void DoMemoryReadout()
        {
            while (running)
            {
                await Task.Delay(100);

                RSMemoryReadout newReadout = null;

                try
                {
                    //Read data from memory
                    newReadout = memReader.DoReadout();
                }
                catch (Exception e)
                {
                    if (running)
                    {
                        Logger.LogError("Error while reading memory: {0} {1}\r\n{2}", e.GetType(), e.Message, e.StackTrace);
                    }
                }

                if (newReadout == null)
                {
                    continue;
                }

                if (newReadout.songID != currentMemoryReadout.songID || (currentCDLCDetails == null || !currentCDLCDetails.IsValid()))
                {
                    var newDetails = cache.Get(newReadout.songID);

                    if (newDetails != null && newDetails.IsValid())
                    {
                        currentCDLCDetails = cache.Get(newReadout.songID);
                        OnSongChanged?.Invoke(this, new OnSongChangedArgs { songDetails = currentCDLCDetails });
                        currentCDLCDetails.Print();
                    }

                }

                newReadout.CopyTo(ref currentMemoryReadout);

                OnMemoryReadout?.Invoke(this, new OnMemoryReadoutArgs() { memoryReadout = currentMemoryReadout });

                //Print memreadout if debug is enabled
                currentMemoryReadout.Print();
            }
        }

        private async void DoStateMachine()
        {
            while (running)
            {
                try
                {
                    //Update the state
                    UpdateState();
                }
                catch (Exception e)
                {
                    if (running)
                    {
                        Logger.LogError("Error while processing state machine: {0} {1}", e.GetType(), e.Message);
                    }
                }

                //Delay for 100 milliseconds
                await Task.Delay(100);
            }
        }

        private async void DoSniffing()
        {
            //Get path to rs directory
            var path = Path.GetDirectoryName(rsProcess.MainModule.FileName);

            watcher = new FileSystemWatcher(path + Path.DirectorySeparatorChar + "dlc", "*.psarc")
            {
                IncludeSubdirectories = true,

                NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
            };

            watcher.Created += PsarcFileChanged;
            watcher.Changed += PsarcFileChanged;
            watcher.Renamed += PsarcFileChanged;
            watcher.Error += Watcher_Error;

            watcher.EnableRaisingEvents = true;

            await Task.Run(() => ProcessAllPsarcs(path));
        }

        private void Watcher_Error(object sender, ErrorEventArgs e)
        {
            Logger.LogError("FileSystemWatcher Error: {0}", e.GetException().Message);
        }

        private void PsarcFileChanged(object sender, FileSystemEventArgs e)
        {
            var psarcFile = e.FullPath;

            if (!cache.Contains(psarcFile))
            {
                //Read psarc data
                Dictionary<string, SongDetails> allSongDetails;
                try
                {
                    allSongDetails = PSARCUtil.ReadPSARCHeaderData(psarcFile);
                }
                catch
                {
                    Logger.LogError("Unable to read {0}", psarcFile);
                    return;
                }

                //If loading failed
                if (allSongDetails == null)
                {
                    //Skip
                    return;
                }

                //Add this CDLC file to the cache
                cache.Add(psarcFile, allSongDetails);
            }
        }

        private void ProcessAllPsarcs(string path)
        {
            Logger.Log("Processing all psarc files");

            var sw = new Stopwatch();
            sw.Start();

            //Build a list of all dlc psarc files, including songs.psarc
            List<string> psarcFiles = new List<string>
            {
                path + "/songs.psarc"
            };

            //Go into the dlc folder
            path = path + "/dlc";

            GetAllPsarcFiles(path, psarcFiles);
            
            Parallel.ForEach(psarcFiles, psarcFile =>
            {
                if (!cache.Contains(psarcFile))
                {
                    //Read psarc data
                    Dictionary<string, SongDetails> allSongDetails;
                    try
                    {
                        allSongDetails = PSARCUtil.ReadPSARCHeaderData(psarcFile);
                    }
                    catch
                    {
                        Logger.LogError("Unable to read {0}", psarcFile);
                        return;
                    }

                    //If loading was successful
                    if (allSongDetails != null)
                    {
                        //Add this CDLC file to the cache
                        cache.Add(psarcFile, allSongDetails);
                    }
                }
            });

            sw.Stop();
            Logger.Log("Processed {0} psarc files in {1}ms", psarcFiles.Count, sw.ElapsedMilliseconds);
        }

        private void GetAllPsarcFiles(string path, List<string> files)
        {
            //Add all files in the current path including all subdirectories
            files.AddRange(Directory.GetFiles(path, "*_p.psarc", SearchOption.AllDirectories));
        }

        /// <summary>
        /// Stops the sniffer, stopping all async tasks
        /// </summary>
        public void Stop()
        {
            running = false;

            watcher.Dispose();
        }

        /// <summary>
        /// Update the state of the sniffer
        /// </summary>
        private void UpdateState()
        {
            //Super complex state machine of state transitions
            switch (currentState)
            {
                case SnifferState.IN_MENUS:
                    if (currentMemoryReadout.songTimer != 0)
                    {
                        currentState = SnifferState.SONG_SELECTED;
                    }
                    break;
                case SnifferState.SONG_SELECTED:
                    if (currentMemoryReadout.songTimer == 0)
                    {
                        currentState = SnifferState.SONG_STARTING;
                    }

                    //If we somehow missed some states, skip to SONG_PLAYING
                    //Or if the user reset
                    if (currentMemoryReadout.songTimer > 1)
                    {
                        currentState = SnifferState.SONG_PLAYING;
                    }
                    break;
                case SnifferState.SONG_STARTING:
                    if (currentMemoryReadout.songTimer > 0)
                    {
                        currentState = SnifferState.SONG_PLAYING;
                    }
                    break;
                case SnifferState.SONG_PLAYING:
                    //Allow 5 seconds of error margin on song ending
                    if (currentMemoryReadout.songTimer >= currentCDLCDetails.songLength - 5)
                    {
                        currentState = SnifferState.SONG_ENDING;
                    }
                    //If the timer goes to 0, the user must have quit
                    if (currentMemoryReadout.songTimer == 0)
                    {
                        currentState = SnifferState.IN_MENUS;
                    }
                    break;
                case SnifferState.SONG_ENDING:
                    if (currentMemoryReadout.songTimer == 0)
                    {
                        currentState = SnifferState.IN_MENUS;
                    }
                    break;
                default:
                    break;
            }

            //Force state to IN_MENUS if the current song details are not valid
            if (!currentCDLCDetails.IsValid())
            {
                currentState = SnifferState.IN_MENUS;
            }

            //If state changed
            if (currentState != previousState)
            {
                //Invoke event
                OnStateChanged?.Invoke(this, new OnStateChangedArgs() { oldState = previousState, newState = currentState });

                //Remember previous state
                previousState = currentState;

                if (Logger.logStateMachine)
                {
                    Logger.Log("Current state: {0}", currentState.ToString());
                }
            }
        }
    }
}
