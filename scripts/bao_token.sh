# Search common spots
grep -r "Root Token\|hvs\.\|s\." ~/.openbao/ ~/.bash_history ~/.zsh_history 2>/dev/null
# Check macOS Keychain
security find-generic-password -s "openbao" 2>/dev/null
# Look in the brew service log if brew started it
ls -la $(brew --prefix)/var/log/openbao* 2>/dev/null
tail $(brew --prefix)/var/log/openbao*.log 2>/dev/null | grep -iE 'root token|unseal'
