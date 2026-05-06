![Mod Logo](https://moddbcdn.vintagestory.at/ModDB+Card_99e77500822135694d92d8a6b1d47262.png)

# No Offline Container Food Spoil

### $\color{#FF0000}\text{Disclaimer}$
This mod does not prevent spoilage in a player's inventory. There is an existing [mod](https://mods.vintagestory.at/offlinefoodnospoil) for this made by [Wiltoga](https://github.com/Wiltoga), and I really don't know how to make a good-enough solution that wouldn't just copy their work. Both mods should be compatible with each other.

This project has been very difficult for me, as I am not a programmer, and the logic that had to be implemented for this to work was very difficult for me to comprehend and work with. I am sorry if it isn't working for you or your mods, although I have tried to cover every edge case in version 2.0. I am exhausted from this project, especially becausing testing this takes so much time, and probably won't continue to work on it. If you find a problem, and want to upload a fix, please submit a pull request and I will try to find time to test it.

---

### Overview
This is a mod created for the game "Vintage Story".

This mod targets an issue with food in Vintage Story multiplayer. In the base game, if a player leaves food in a container and then leaves the server, it will gradually spoil over time. Players that spend large amounts of time on servers can negatively affect other player's experience as by the time they log back on, everything they have will be turned into rot.

### How it works
Containers blocks have custom behavior attached to them that tracks who interacts with it, separating players into 2 categories:
- **residents**: Players who own this container and interact with it often, probably members of the same household.
- **provisional**: Players who interact with container just once, adding/removing items from it. 

Both types cause food to spoil if ANY of them is online, but provisional players are removed from the tracking system after much shorter period of time.  
There always must be at least one resident added to the container, other residents can also "expiry" if they don't interact with the container for a long time, especially if they are far away from it.  

All these values are configurable. Including the spoil rate when players are offline - currently default is 0.05.

This system still could be abused if one of players just on purpose never interacts with the container, maybe this can be improved in the future by automatically adding players who are close to container frequently.  
But by keeping the list of all players who take/put items in/out of the container I think this is a good compromise to keep the system simple for most cases.  
This current system (by GotoFinal) was mostly designed for servers with few small groups that might play in different times of the day, so abuse prevention was not a big concern.

There is also `/spoildebug` command that can be used to see players added to the storage and even add/remove them manually.
---

### Credit
This was not my idea. I knew this was a problem and was looking for a solution, and credit for this idea goes fully to [Tabulius](https://www.vintagestory.at/profile/347446-tabulius/). The post I found their idea on is [here](https://www.vintagestory.at/forums/topic/18000-mod-request-solution-to-offline-food-perishing-in-multiplayer/).

Releases for this can be found on Vintage Story's [Mod Portal](https://mods.vintagestory.at/noofflinecontainerfoodspoil).
