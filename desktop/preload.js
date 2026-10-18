// What the app's own pages may ask the desktop shell for (window.abeatDesktop in the UI).
const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('abeatDesktop', {
  /** { signedIn: boolean } for the in-app YouTube sign-in */
  youtubeStatus: () => ipcRenderer.invoke('youtube:status'),
  /** Opens the YouTube sign-in window; resolves with the status once it is closed. */
  youtubeSignIn: () => ipcRenderer.invoke('youtube:sign-in'),
  youtubeSignOut: () => ipcRenderer.invoke('youtube:sign-out'),
})
