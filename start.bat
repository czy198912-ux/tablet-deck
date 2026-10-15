@echo off
title Tablet Deck
cd /d "%~dp0"
node server.js %*
pause
