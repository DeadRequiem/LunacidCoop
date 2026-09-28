# LunacidCoop
This mod is a massive WIP attempt at converting the singleplayer game Lunacid by Kira into pseudo-coop (kind of). ---- This doc is in a WIP state as well and I will add on and work on it more as I have free time to do so.

Most of the upper half of this doc will be explaining the mod, how it works and functions, and the later half will be the more technical details of the mod itself. The ending of this doc will be issues and technical limitations that I myself am aware of. Obviously, none of this is set in stone and is all subject to change, from the ordering to the description and wording of the mod itself.
With that said, let's get into it, starting with...

## The Mod Itself
In the previous section I mentioned that this mod is a WIP & "Pseudo-coop", well what does that mean, exactly?

### W.I.P.
In regards to W(ork) I(n) P(rogress), it should be self-explanatory; this mod is extremely experimental and bound to have issues such as crashing, softlocks, data loss and potentially countless others that I have no way to account for outside of them being submitted to me or experiencing the issue myself.
This also means that the mod and all code in it is subject to change drastically. There are no guarantees that things that work in one version will work in the next, but it can also go the other way, in that things that *did not* work in the previous version might work in the updated one.
That being said, I do try my best *not* to break things if at all possible, and do try my best to extensively test whatever it is that I change before submitting an update, but I am only one person.

### Pseudo-coop
I am using the term "Pseudo-coop" to describe this mod to differentiate it from the various **types** of coop.
Unlike the traditional understanding of most coops, this mod **does not** sync player data, saves, progression, or anything related to each individual players save game.
Instead, this mod functions on "Host Authorization" in a very loose sense and runs off of the "Host & Client" ideology.
To explain: the world state is driven by the host. Enemies/monsters (from this point onward referred to as just "NPCs"; this does not include talking NPCs, which are covered later) are controlled entirely by the host. Breakable objects and physical props are also sync'd between players, as are toggles and levers.

Player **1** "hosts" the lobby for player **2** to join as a "client".
Player **2** (client) may travel freely anywhere they personally have unlocked in terms of progression without player **1** (host).
However, without player **1** (host) in the same area, all NPCs will be static and unable to be damaged by player **2**.
This is done to prevent sync issues between players.
This also means that when player **1** enters an area without player **2**, NPCs that player **1** defeats will already be defeated when player **2** enters the area.

### Hosting & Joining
todo

## Gameplay Details
### Items
todo
### Enemy Scaling
todo
### Combat
todo

## Technical
### NPCs
todo
### Other Players
todo
### Requirements & Install
todo
### Config Options
In terms of config options, there's a lot of variety here, from debugging and logging to gameplay options. Most of which came out of necessity, such as ItemsPerPlayer, but others are completely optional difficulty tweaks for better gameplay experience, below is a list of them all and a brief explanation as to what they are for
(Explanations are to do right now, will fill in when not tired as I am writing this on no sleep. Does anyone actually read this?)
#### Debug:
LocalTestMode
#### Display:
VSyncCount
#### Gameplay:
ItemsPerPlayer
ScaleEnemyHP
HPScalePerPlayer
#### Logging:
EnableLogging
LogCoopRigidbody
LogSpellSync
LogWeaponSync
LogNPCScanner
LogMPMenu
LogPlayerVisuals
LogPlayerRegistry
LogWorldSync
LogNetSend
LogNetRecv
#### Sync:
PlayerSendRateHz
AutoReconnect





### Game Pausing
This one may come as a bit of a surprise as it's not a typical feature people would notice initially.
Lunacid, like a lot of games, limits itself whenever the game isn't the main window, such as being tabbed out or minimized. Lunacid specifically pauses itself entirely, it sets the entire game to 0 while tabbed out. This mod stops that upon Hosting or Joining a game, at either point the game will no longer pause when tabbed out; This is for fairly obvious reasons as to not prevent connection timeouts and fake ping issues that result in a connection boot
todo: adding the actual technical part for this, it's late and I'm so v tired pls forgive I fix soon(tm)

### Known Issues, Limitations & Mod Compatibility
I actually have a massive list of all the possible issues that players could face and will slowly fill them in as time goes on, as for limitations or compatibility issues.. I can't really think of many? Obviously anything that would need to sync players or animations would need some kind of patch but off the top of my head I can't think of any that would! (There was recently a map mod that was created, that is on my todo list to check!).
As I go through mods and verify that they work or don't I will add a new section below this one to confirm the mods that work and don't.

## How To Report Issues
This one is, hopefully fairly straight forward. Where as I intend to provide more options for such, there is currently only one method of reporting an issue to me and that is here on Github ( Again, this will be expanded later whenever it is that I actually... have more methods. )
If you have never used Github before, you can just go here:
https://github.com/DeadRequiem/LunacidCoop/issues
Title your issue, attach any relevant images and leave me a way to reproduce the issue you are having, or at the very least describe to me what it is that you did to get the issue, as well as what the issue itself is, and I'll try my best to get it and help you solve the issue, or even fix the code if need be.

## Gallery & End notes
The following is less detail of the mod and more imagery of the mod itself in various stages of creation, progression and in use. Some may have already been used above, but this is a collection of all of them regardless. This mod has gone through a lot of changes and variations over the years and I tried to take screenshots of it as I went to remind myself where it is that I started and where I still could improved and work on. This mod was made out of a passion and love for the game itself, Lunacid's environment truthfully is one of my favorites, with some of the soundtracks like Falling To Death being part of my main playlist. My hope is that others can enjoy playing around with this mod and enjoy the game with friends.

<img width="50%" alt="1" src="https://github.com/user-attachments/assets/65ac553e-ea8b-43ec-8da1-315b6d5bfe6a" />

<img width="50%" alt="2" src="https://github.com/user-attachments/assets/99a7dbcf-a920-4e08-bcc0-957d4a75ae30" />

<img width="50%" alt="3" src="https://github.com/user-attachments/assets/bbff4352-326a-4452-8d62-47f2d235628b" />

<img width="50%" alt="4" src="https://github.com/user-attachments/assets/d0ee6918-4dd5-4b2b-92f3-0285df1998f7" />

<img width="50%" alt="5" src="https://github.com/user-attachments/assets/231f86fd-99f3-4772-9992-3c02adcbd8ea" />

<img width="50%" alt="6" src="https://github.com/user-attachments/assets/e0c8ff2d-0085-45d3-b10e-bd5a80c656ff" />

<img width="50%" alt="7" src="https://github.com/user-attachments/assets/bc4bd1e4-7f93-4f2a-a44b-c15f540be1f1" />

<img width="50%" alt="8" src="https://github.com/user-attachments/assets/3bdc4442-c08c-4e86-b9e3-e2ee1dc41c72" />

<img width="50%" alt="9" src="https://github.com/user-attachments/assets/72977102-5d52-4752-ab1a-e83c90dc5aa5" />

<img width="50%" alt="12" src="https://github.com/user-attachments/assets/dea9df7f-db6e-4d65-93b6-5ddd0749066d" />

<img width="50%" alt="13" src="https://github.com/user-attachments/assets/9e2d7400-8df9-426a-9ef3-58a7a3dbf4aa" />

<img width="50%" alt="14" src="https://github.com/user-attachments/assets/44d76131-95ac-4bb1-852e-cd5ddf22715d" />

<img width="50%" alt="15" src="https://github.com/user-attachments/assets/bdac78c9-0918-4d2f-b514-a3a3cb7bac35" />

<img width="50%" alt="16" src="https://github.com/user-attachments/assets/101342ad-8fb7-45aa-9a95-ae7c4559e9a5" />


