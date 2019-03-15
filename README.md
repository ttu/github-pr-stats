# GitHub PR Statistics

Create statistics of user's pull requests

#### Uses

##### Polly 

##### FluentScheduler

### GitHub API


Pull requests from specific user
```
https://api.github.com/search/issues?q=author%3Attu+type%3Apr
```

Define page
```
https://api.github.com/search/issues?q=author%3Attu+type%3Apr&page=2
```

List users

```
https://api.github.com/search/users?q=location:finland
```

List users with repo count greater than 5
```
https://api.github.com/search/users?q=location:finland+repos:%3E5
```