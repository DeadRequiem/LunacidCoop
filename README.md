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
